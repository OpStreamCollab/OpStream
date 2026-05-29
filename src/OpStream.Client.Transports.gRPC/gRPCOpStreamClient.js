/**
 * A gRPC-based implementation of the OpStream client transport for HTML + JS clients.
 * Handles bidirectional streaming and request-response patterns over a single stream.
 * Note: Bidirectional streaming requires a compatible transport in the browser (e.g., websockets).
 */
export class gRPCOpStreamClient {
    /**
     * Initializes a new instance of the gRPCOpStreamClient.
     * @param {string} address The URL of the gRPC server.
     * @param {Object} options Configuration options to provide the generated classes.
     * @param {any} options.ClientClass The generated OpStreamServicePromiseClient class.
     * @param {any} options.CommentsClientClass The generated OpStreamCommentsServicePromiseClient class.
     * @param {any} options.Messages An object containing the generated protobuf message classes.
     */
    constructor(address, options = {}) {
        const ClientClass = options.ClientClass || (typeof window !== 'undefined' ? window.OpStreamServicePromiseClient : null);
        const CommentsClientClass = options.CommentsClientClass || (typeof window !== 'undefined' ? window.OpStreamCommentsServicePromiseClient : null);
        this.Messages = options.Messages || (typeof window !== 'undefined' ? window : null);

        if (!ClientClass || !CommentsClientClass || !this.Messages) {
            throw new Error("gRPC-Web client classes not found. Ensure they are loaded globally or provided in the options.");
        }

        // Equivalent to Guid.NewGuid()
        this._peerId = typeof crypto !== 'undefined' && crypto.randomUUID 
            ? crypto.randomUUID() 
            : Math.random().toString(36).substring(2, 15);

        this._client = new ClientClass(address);
        this._commentsClient = new CommentsClientClass(address);
        
        this._pendingRequests = new Map();

        // ── Events ────────────────────────────────────────────────────────
        this.onReceiveOp = null;
        this.onDisconnected = null;
        this.onReceiveAwareness = null;
        this.onPeerDisconnected = null;

        this.onCommentCreated = null;
        this.onCommentUpdated = null;
        this.onCommentDeleted = null;

        // Initialize bidirectional stream
        // Note: For standard grpc-web in browser, bidirectional stream requires specific transport (like @improbable-eng/grpc-web websocket)
        if (typeof this._client.connect === 'function') {
            this._call = this._client.connect();
            this._listenLoop();
        } else {
            console.warn("connect() method not found on client. Bidirectional streaming might not be supported by this stub.");
        }
    }

    _listenLoop() {
        if (!this._call || typeof this._call.on !== 'function') return;

        this._call.on('data', async (message) => {
            const correlationId = message.getCorrelationId();
            if (correlationId && this._pendingRequests.has(correlationId)) {
                const pending = this._pendingRequests.get(correlationId);
                this._pendingRequests.delete(correlationId);
                pending.resolve(message);
                return; // Handled as a request-response
            }

            // Handle server push events
            if (message.hasReceiveOpEvent()) {
                if (this.onReceiveOp) {
                    const evt = message.getReceiveOpEvent();
                    await this.onReceiveOp(evt.getPayload(), evt.getNewRevision());
                }
            } else if (message.hasReceiveAwarenessEvent()) {
                if (this.onReceiveAwareness) {
                    const evt = message.getReceiveAwarenessEvent();
                    const states = evt.getAwarenessList().map(p => this._fromAwarenessProto(p));
                    await this.onReceiveAwareness(states);
                }
            } else if (message.hasPeerDisconnectedEvent()) {
                if (this.onPeerDisconnected) {
                    const evt = message.getPeerDisconnectedEvent();
                    this.onPeerDisconnected(evt.getPeerId());
                }
            }
        });

        this._call.on('error', (err) => {
            if (this.onDisconnected) {
                this.onDisconnected(err);
            }
        });

        this._call.on('end', () => {
            if (this.onDisconnected) {
                this.onDisconnected(new Error("Stream ended by server"));
            }
        });
    }

    /**
     * Connects to the document session.
     */
    async connectAndJoinAsync(documentId, documentType) {
        const correlationId = typeof crypto !== 'undefined' && crypto.randomUUID ? crypto.randomUUID() : Math.random().toString(36).substring(2);
        
        return new Promise((resolve, reject) => {
            this._pendingRequests.set(correlationId, { resolve, reject });

            const joinRequest = new this.Messages.JoinRequest();
            joinRequest.setDocumentId(documentId);
            joinRequest.setDocumentType(documentType);
            joinRequest.setClientProtoVersion(1);

            const clientMessage = new this.Messages.ClientMessage();
            clientMessage.setCorrelationId(correlationId);
            clientMessage.setJoinRequest(joinRequest);

            this._call.write(clientMessage);
        }).then((response) => {
            if (!response.hasJoinResponse()) {
                throw new Error("Unexpected response during Join");
            }

            const jr = response.getJoinResponse();
            
            // Start listening to comments for this document
            this._listenCommentsLoop(documentId);

            return {
                revision: jr.getRevision(),
                snapshot: jr.getSnapshot(),
                currentAwareness: jr.getAwarenessList().map(p => this._fromAwarenessProto(p))
            };
        });
    }

    /**
     * Sends an operation payload.
     */
    async sendOpAsync(documentId, payload, baseRevision) {
        const correlationId = typeof crypto !== 'undefined' && crypto.randomUUID ? crypto.randomUUID() : Math.random().toString(36).substring(2);

        return new Promise((resolve, reject) => {
            this._pendingRequests.set(correlationId, { resolve, reject });

            const opRequest = new this.Messages.OpRequest();
            opRequest.setDocumentId(documentId);
            opRequest.setPayload(payload);
            opRequest.setBaseRevision(baseRevision);

            const clientMessage = new this.Messages.ClientMessage();
            clientMessage.setCorrelationId(correlationId);
            clientMessage.setOpRequest(opRequest);

            this._call.write(clientMessage);
        }).then((response) => {
            if (!response.hasOpResponse()) {
                throw new Error("Unexpected response during SendOp");
            }

            const or = response.getOpResponse();
            return {
                success: or.getSuccess(),
                newRevision: or.getNewRevision(),
                errorMessage: or.getErrorMessage()
            };
        });
    }

    /**
     * Sends an awareness update payload.
     */
    async sendAwarenessAsync(documentId, data) {
        const awarenessRequest = new this.Messages.AwarenessRequest();
        awarenessRequest.setDocumentId(documentId);
        // Supports stringified JSON or an object
        awarenessRequest.setDataJson(typeof data === 'string' ? data : JSON.stringify(data));

        const clientMessage = new this.Messages.ClientMessage();
        clientMessage.setCorrelationId(""); // No correlation needed for one-way messages
        clientMessage.setAwarenessRequest(awarenessRequest);

        this._call.write(clientMessage);
    }

    // ─── Comment subscription ─────────────────────────────────────────────────

    _listenCommentsLoop(documentId) {
        if (this._commentCall && typeof this._commentCall.cancel === 'function') {
            this._commentCall.cancel();
        }

        const request = new this.Messages.SubscribeCommentsRequest();
        request.setDocumentId(documentId);

        this._commentCall = this._commentsClient.subscribeComments(request);
        
        if (typeof this._commentCall.on === 'function') {
            this._commentCall.on('data', async (evt) => {
                if (evt.hasCreated() && this.onCommentCreated) {
                    await this.onCommentCreated(this._protoToCommentDto(evt.getCreated()));
                } else if (evt.hasUpdated() && this.onCommentUpdated) {
                    await this.onCommentUpdated(this._protoToCommentDto(evt.getUpdated()));
                } else if (evt.hasDeletedCommentId() && this.onCommentDeleted) {
                    await this.onCommentDeleted({ deletedCommentId: evt.getDeletedCommentId() });
                }
            });

            this._commentCall.on('error', (err) => {
                // Typically cancelled by disposing, ignore
            });
        }
    }

    // ─── Comment methods ──────────────────────────────────────────────────────

    async listOpenCommentsAsync(documentId) {
        const request = new this.Messages.ListCommentsRequest();
        request.setDocumentId(documentId);

        const response = await this._commentsClient.listOpenComments(request);
        return response.getCommentsList().map(p => this._protoToCommentDto(p));
    }

    async createCommentAsync(documentId, cmd) {
        const request = new this.Messages.CreateCommentRequest();
        request.setPeerId(this._peerId);
        request.setDocumentId(documentId);
        request.setBody(cmd.body || cmd.Body);
        
        const anchor = cmd.anchor || cmd.Anchor;
        request.setAnchorJson(anchor ? JSON.stringify(anchor) : "");
        
        const parentId = cmd.parentCommentId || cmd.ParentCommentId;
        request.setParentCommentId(parentId || "");

        const response = await this._commentsClient.createComment(request);
        if (!response.getSuccess()) {
            throw new Error(response.getErrorMessage());
        }
        return this._protoToCommentDto(response.getComment());
    }

    async editCommentAsync(documentId, commentId, newBody) {
        const request = new this.Messages.EditCommentRequest();
        request.setPeerId(this._peerId);
        request.setDocumentId(documentId);
        request.setCommentId(commentId);
        request.setNewBody(newBody);

        const response = await this._commentsClient.editComment(request);
        if (!response.getSuccess()) {
            throw new Error(response.getErrorMessage());
        }
        return this._protoToCommentDto(response.getComment());
    }

    async resolveCommentAsync(documentId, commentId) {
        const request = new this.Messages.CommentActionRequest();
        request.setPeerId(this._peerId);
        request.setDocumentId(documentId);
        request.setCommentId(commentId);

        const response = await this._commentsClient.resolveComment(request);
        if (!response.getSuccess()) {
            throw new Error(response.getErrorMessage());
        }
        return this._protoToCommentDto(response.getComment());
    }

    async deleteCommentAsync(documentId, commentId) {
        const request = new this.Messages.CommentActionRequest();
        request.setPeerId(this._peerId);
        request.setDocumentId(documentId);
        request.setCommentId(commentId);

        const response = await this._commentsClient.deleteComment(request);
        if (!response.getSuccess()) {
            throw new Error(response.getErrorMessage());
        }
    }

    // ─── Mapping ──────────────────────────────────────────────────────────────

    _protoToCommentDto(p) {
        const anchorJson = p.getAnchorJson();
        const resolvedAtProto = p.getResolvedAt();
        
        return {
            id: p.getId(),
            documentId: p.getDocumentId(),
            parentCommentId: p.getParentCommentId() || null,
            authorPeerId: p.getAuthorPeerId(),
            body: p.getBody(),
            anchor: anchorJson ? JSON.parse(anchorJson) : null,
            anchoredAtRevision: p.getAnchoredAtRevision(),
            createdAt: this._toDateTime(p.getCreatedAt()),
            resolvedAt: resolvedAtProto ? this._toDateTime(resolvedAtProto) : null,
            resolvedByPeerId: p.getResolvedByPeerId() || null,
            isOrphaned: p.getIsOrphaned()
        };
    }

    _fromAwarenessProto(proto) {
        return {
            peerId: proto.getPeerId(),
            data: JSON.parse(proto.getDataJson()),
            lastUpdated: this._toDateTime(proto.getLastUpdated())
        };
    }

    _toDateTime(protoTimestamp) {
        if (!protoTimestamp) return null;
        if (typeof protoTimestamp.toDate === 'function') {
            return protoTimestamp.toDate();
        }
        const secs = protoTimestamp.getSeconds ? protoTimestamp.getSeconds() : protoTimestamp.seconds || 0;
        const nanos = protoTimestamp.getNanos ? protoTimestamp.getNanos() : protoTimestamp.nanos || 0;
        return new Date((secs * 1000) + (nanos / 1000000));
    }

    async disposeAsync() {
        if (this._call && typeof this._call.cancel === 'function') {
            this._call.cancel();
        }
        if (this._commentCall && typeof this._commentCall.cancel === 'function') {
            this._commentCall.cancel();
        }
        
        for (const pending of this._pendingRequests.values()) {
            pending.reject(new Error("Client disposed"));
        }
        this._pendingRequests.clear();
        return Promise.resolve();
    }
}
