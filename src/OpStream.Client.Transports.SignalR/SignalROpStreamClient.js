import * as signalR from "@microsoft/signalr";

/**
 * A SignalR-based implementation of the OpStream client transport for HTML + JS clients.
 * Provides the exact same functionality as the C# SignalROpStreamClient class.
 */
export class SignalROpStreamClient {
    /**
     * Initializes a new instance of the SignalROpStreamClient.
     * @param {string} hubUrl The URL of the SignalR hub.
     */
    constructor(hubUrl) {
        let Builder;
        if (typeof signalR !== 'undefined' && signalR.HubConnectionBuilder) {
            Builder = signalR.HubConnectionBuilder;
        } else if (typeof window !== 'undefined' && window.signalR && window.signalR.HubConnectionBuilder) {
            Builder = window.signalR.HubConnectionBuilder;
        }

        if (!Builder) {
            throw new Error("SignalR is not available. Please ensure @microsoft/signalr is installed or loaded via CDN.");
        }

        this._hubConnection = new Builder()
            .withUrl(hubUrl)
            .withAutomaticReconnect()
            .build();

        // ─── Events ────────────────────────────────────────────────────────
        
        this.onReceiveOp = null;               // (payload: Uint8Array | string, newRev: number) => Promise<void> | void
        this.onDisconnected = null;            // (error: Error) => void
        this.onReceiveAwareness = null;        // (states: Array<any>) => Promise<void> | void
        this.onPeerDisconnected = null;        // (peerId: string) => void
        this.onCommentCreated = null;          // (comment: any) => Promise<void> | void
        this.onCommentUpdated = null;          // (comment: any) => Promise<void> | void
        this.onCommentDeleted = null;          // (msg: any) => Promise<void> | void

        // ─── Wire up Hub events ────────────────────────────────────────────
        
        this._hubConnection.on("ReceiveOp", async (payload, newRev) => {
            if (this.onReceiveOp) {
                await this.onReceiveOp(payload, newRev);
            }
        });

        this._hubConnection.on("ReceiveAwarenessUpdate", async (state) => {
            if (this.onReceiveAwareness) {
                // The C# client wraps the single state into an array to return IEnumerable
                await this.onReceiveAwareness([state]);
            }
        });

        this._hubConnection.on("PeerDisconnected", async (peerId) => {
            if (this.onPeerDisconnected) {
                await this.onPeerDisconnected(peerId);
            }
        });

        this._hubConnection.on("ReceiveCommentCreated", async (comment) => {
            if (this.onCommentCreated) {
                await this.onCommentCreated(comment);
            }
        });

        this._hubConnection.on("ReceiveCommentUpdated", async (comment) => {
            if (this.onCommentUpdated) {
                await this.onCommentUpdated(comment);
            }
        });

        this._hubConnection.on("ReceiveCommentDeleted", async (msg) => {
            if (this.onCommentDeleted) {
                await this.onCommentDeleted(msg);
            }
        });

        this._hubConnection.onclose((error) => {
            if (this.onDisconnected) {
                this.onDisconnected(error);
            }
        });
    }

    /**
     * Connects to the hub and joins a document session.
     * @param {string} documentId 
     * @param {string} documentType 
     * @returns {Promise<{revision: number, snapshot: Uint8Array | string, currentAwareness: Array<any>}>}
     */
    async connectAndJoinAsync(documentId, documentType) {
        await this._hubConnection.start();
        
        // Handshake: documentId, documentType, protocolVersion = 1
        const result = await this._hubConnection.invoke("JoinDocument", documentId, documentType, 1);
        
        return {
            revision: result.revision ?? result.Revision,
            snapshot: result.snapshot ?? result.Snapshot,
            currentAwareness: result.currentAwareness ?? result.CurrentAwareness ?? []
        };
    }

    /**
     * Sends an operation to the server.
     * @param {string} documentId 
     * @param {Uint8Array | string | Array} payload 
     * @param {number} baseRevision 
     * @returns {Promise<{success: boolean, newRevision: number, errorMessage: string | null}>}
     */
    async sendOpAsync(documentId, payload, baseRevision) {
        const result = await this._hubConnection.invoke("SendOp", documentId, payload, baseRevision);
        return {
            success: result.success !== undefined ? result.success : result.Success,
            newRevision: result.newRevision ?? result.NewRevision,
            errorMessage: result.errorMessage ?? result.ErrorMessage
        };
    }

    /**
     * Sends an awareness update to the server.
     * @param {string} documentId 
     * @param {any} data 
     * @returns {Promise<void>}
     */
    async sendAwarenessAsync(documentId, data) {
        await this._hubConnection.invoke("UpdateAwareness", documentId, data);
    }

    // ─── Comments ────────────────────────────────────────────────────────

    /**
     * Lists all open comments for a document.
     * @param {string} documentId 
     * @returns {Promise<Array<any>>}
     */
    async listOpenCommentsAsync(documentId) {
        return await this._hubConnection.invoke("ListOpenComments", documentId);
    }

    /**
     * Creates a new comment.
     * @param {string} documentId 
     * @param {any} cmd 
     * @returns {Promise<any>}
     */
    async createCommentAsync(documentId, cmd) {
        return await this._hubConnection.invoke("CreateComment", documentId, cmd);
    }

    /**
     * Edits an existing comment.
     * @param {string} documentId 
     * @param {string} commentId 
     * @param {string} newBody 
     * @returns {Promise<any>}
     */
    async editCommentAsync(documentId, commentId, newBody) {
        return await this._hubConnection.invoke("EditComment", documentId, commentId, newBody);
    }

    /**
     * Resolves an existing comment.
     * @param {string} documentId 
     * @param {string} commentId 
     * @returns {Promise<any>}
     */
    async resolveCommentAsync(documentId, commentId) {
        return await this._hubConnection.invoke("ResolveComment", documentId, commentId);
    }

    /**
     * Deletes a comment.
     * @param {string} documentId 
     * @param {string} commentId 
     * @returns {Promise<void>}
     */
    async deleteCommentAsync(documentId, commentId) {
        await this._hubConnection.invoke("DeleteComment", documentId, commentId);
    }

    /**
     * Disposes the client by stopping the hub connection.
     * @returns {Promise<void>}
     */
    async disposeAsync() {
        await this._hubConnection.stop();
    }
}
