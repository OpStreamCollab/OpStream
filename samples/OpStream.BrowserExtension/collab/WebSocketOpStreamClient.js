/**
 * A WebSocket-based implementation of the OpStream client transport for HTML + JS clients.
 * Handles bidirectional communication using JSON messages over a single WebSocket.
 *
 * NOTE: copied verbatim from samples/MonacoCollaborativeJs/js/WebSocketOpStreamClient.js
 * so the browser extension is self-contained and packageable.
 */
export class WebSocketOpStreamClient {
    constructor(serverUri) {
        this.serverUri = serverUri;
        this._webSocket = null;
        this._pendingRequests = new Map();

        // ── Events ────────────────────────────────────────────────────────
        this.onReceiveOp = null;
        this.onDisconnected = null;
        this.onReceiveAwareness = null;
        this.onPeerDisconnected = null;

        this.onCommentCreated = null;
        this.onCommentUpdated = null;
        this.onCommentDeleted = null;

        // Maps to C# WebSocketOpMessageType enum
        this.MessageType = {
            JoinRequest: 0,
            JoinResponse: 1,
            OpRequest: 2,
            OpResponse: 3,
            AwarenessRequest: 4,
            ReceiveOpEvent: 5,
            ReceiveAwarenessEvent: 6,
            PeerDisconnectedEvent: 7,
            ErrorResponse: 8,
            CreateComment: 9,
            EditComment: 10,
            ResolveComment: 11,
            DeleteComment: 12,
            ListOpenComments: 13,
            ReceiveCommentCreated: 14,
            ReceiveCommentUpdated: 15,
            ReceiveCommentDeleted: 16
        };

        this._connect();
    }

    _connect() {
        if (typeof WebSocket === 'undefined') {
            throw new Error("WebSocket is not supported in this environment.");
        }

        this._webSocket = new WebSocket(this.serverUri);

        this._webSocket.onmessage = async (event) => {
            try {
                const message = JSON.parse(event.data);

                if (message.correlationId && this._pendingRequests.has(message.correlationId)) {
                    const pending = this._pendingRequests.get(message.correlationId);
                    this._pendingRequests.delete(message.correlationId);
                    pending.resolve(message);
                    return;
                }

                switch (message.messageType) {
                    case this.MessageType.ReceiveOpEvent:
                    case "ReceiveOpEvent":
                        if (this.onReceiveOp && message.receiveOpEvent) {
                            await this.onReceiveOp(message.receiveOpEvent.payload, message.receiveOpEvent.newRevision);
                        }
                        break;
                    case this.MessageType.ReceiveAwarenessEvent:
                    case "ReceiveAwarenessEvent":
                        if (this.onReceiveAwareness && message.receiveAwarenessEvent) {
                            await this.onReceiveAwareness(message.receiveAwarenessEvent.awareness);
                        }
                        break;
                    case this.MessageType.PeerDisconnectedEvent:
                    case "PeerDisconnectedEvent":
                        if (this.onPeerDisconnected && message.peerDisconnectedEvent) {
                            this.onPeerDisconnected(message.peerDisconnectedEvent.peerId);
                        }
                        break;
                    case this.MessageType.ReceiveCommentCreated:
                    case "ReceiveCommentCreated":
                        if (this.onCommentCreated && message.receiveCommentCreated) {
                            await this.onCommentCreated(message.receiveCommentCreated);
                        }
                        break;
                    case this.MessageType.ReceiveCommentUpdated:
                    case "ReceiveCommentUpdated":
                        if (this.onCommentUpdated && message.receiveCommentUpdated) {
                            await this.onCommentUpdated(message.receiveCommentUpdated);
                        }
                        break;
                    case this.MessageType.ReceiveCommentDeleted:
                    case "ReceiveCommentDeleted":
                        if (this.onCommentDeleted && message.receiveCommentDeleted) {
                            await this.onCommentDeleted({ deletedCommentId: message.receiveCommentDeleted.commentId });
                        }
                        break;
                }
            } catch (ex) {
                console.error("Failed to process WebSocket message", ex);
            }
        };

        this._webSocket.onclose = (event) => {
            if (this.onDisconnected) {
                this.onDisconnected(new Error(`WebSocket closed. Code: ${event.code}`));
            }
        };

        this._webSocket.onerror = () => {
            if (this.onDisconnected) {
                this.onDisconnected(new Error("WebSocket error occurred."));
            }
        };
    }

    async _waitForConnection() {
        if (!this._webSocket) return;
        if (this._webSocket.readyState === 1) return;

        return new Promise((resolve, reject) => {
            const check = () => {
                if (this._webSocket.readyState === 1) {
                    resolve();
                } else if (this._webSocket.readyState > 1) {
                    reject(new Error("WebSocket is not open"));
                } else {
                    setTimeout(check, 10);
                }
            };
            check();
        });
    }

    async connectAndJoinAsync(documentId, documentType) {
        await this._waitForConnection();

        if (this._webSocket.readyState !== 1) {
            throw new Error("WebSocket is not open");
        }

        const correlationId = this._generateId();
        const request = {
            correlationId: correlationId,
            messageType: this.MessageType.JoinRequest,
            joinRequest: {
                documentId: documentId,
                documentType: documentType,
                clientProtoVersion: 1
            }
        };

        const response = await this._sendMessageAsync(request, correlationId);

        if (response.messageType === this.MessageType.ErrorResponse || response.messageType === "ErrorResponse") {
            throw new Error(response.errorMessage || "Join failed.");
        }
        if (response.messageType !== this.MessageType.JoinResponse && response.messageType !== "JoinResponse") {
            throw new Error("Unexpected response during Join");
        }

        const jr = response.joinResponse;
        return {
            revision: jr.revision,
            snapshot: jr.snapshot,
            currentAwareness: jr.awareness || []
        };
    }

    async sendOpAsync(documentId, payload, baseRevision) {
        const correlationId = this._generateId();

        let encodedPayload = payload;
        if (payload instanceof Uint8Array || payload instanceof ArrayBuffer) {
            encodedPayload = this._arrayBufferToBase64(payload);
        }

        const request = {
            correlationId: correlationId,
            messageType: this.MessageType.OpRequest,
            opRequest: {
                documentId: documentId,
                payload: encodedPayload,
                baseRevision: baseRevision
            }
        };

        const response = await this._sendMessageAsync(request, correlationId);

        if (response.messageType === this.MessageType.ErrorResponse || response.messageType === "ErrorResponse") {
            throw new Error(response.errorMessage || "SendOp failed.");
        }
        if (response.messageType !== this.MessageType.OpResponse && response.messageType !== "OpResponse") {
            throw new Error("Unexpected response during SendOp");
        }

        const or = response.opResponse;
        return {
            success: or.success,
            newRevision: or.newRevision,
            errorMessage: or.errorMessage
        };
    }

    async sendAwarenessAsync(documentId, data) {
        const request = {
            messageType: this.MessageType.AwarenessRequest,
            awarenessRequest: {
                documentId: documentId,
                dataJson: typeof data === 'string' ? data : JSON.stringify(data)
            }
        };
        this._webSocket.send(JSON.stringify(request));
    }

    async _sendMessageAsync(message, correlationId) {
        return new Promise((resolve, reject) => {
            this._pendingRequests.set(correlationId, { resolve, reject });
            this._webSocket.send(JSON.stringify(message));
        });
    }

    async disposeAsync() {
        if (this._webSocket) {
            if (this._webSocket.readyState === 1 || this._webSocket.readyState === 0) {
                this._webSocket.close(1000, "Disposing");
            }
        }
        for (const pending of this._pendingRequests.values()) {
            pending.reject(new Error("Client disposed"));
        }
        this._pendingRequests.clear();
        return Promise.resolve();
    }

    _generateId() {
        return typeof crypto !== 'undefined' && crypto.randomUUID
            ? crypto.randomUUID()
            : Math.random().toString(36).substring(2, 15);
    }

    _arrayBufferToBase64(buffer) {
        let binary = '';
        const bytes = new Uint8Array(buffer);
        const len = bytes.byteLength;
        for (let i = 0; i < len; i++) {
            binary += String.fromCharCode(bytes[i]);
        }
        return typeof btoa !== 'undefined' ? btoa(binary) : Buffer.from(buffer).toString('base64');
    }
}
