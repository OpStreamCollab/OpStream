/**
 * gRPC implementation of the OpStream management client for HTML + JS clients.
 * Uses the generated gRPC-Web PromiseClient stubs.
 * "Forbidden:" errors from the server are surfaced with an isForbidden flag.
 */
export class gRPCOpStreamManagementClient {
    /**
     * Initializes the client.
     * @param {string} address The URL of the gRPC-Web proxy or server.
     * @param {Object} options Configuration options to provide the generated classes.
     * @param {any} options.MgmtClientClass The generated OpStreamManagementServicePromiseClient class.
     * @param {any} options.VerClientClass The generated OpStreamVersioningServicePromiseClient class.
     * @param {any} options.Messages An object containing the generated protobuf message classes.
     */
    constructor(address, options = {}) {
        const MgmtClient = options.MgmtClientClass || (typeof window !== 'undefined' ? window.OpStreamManagementServicePromiseClient : null);
        const VerClient = options.VerClientClass || (typeof window !== 'undefined' ? window.OpStreamVersioningServicePromiseClient : null);
        this.Messages = options.Messages || (typeof window !== 'undefined' ? window : null);

        if (!MgmtClient || !VerClient || !this.Messages) {
            throw new Error("gRPC-Web client classes not found. Ensure they are loaded globally or provided in the options.");
        }

        this._channelAddress = address;
        this._mgmt = new MgmtClient(address);
        this._ver = new VerClient(address);
    }

    /**
     * Connects to the service. gRPC-Web is lazy-connect, so this resolves immediately.
     */
    async connectAsync() {
        return Promise.resolve();
    }

    // ── Documents / history ───────────────────────────────────────────────────

    async listDocumentsAsync(query) {
        const request = new this.Messages.MgmtListDocumentsRequest();
        if (query && query.skip !== undefined && query.skip !== null) {
            request.setSkip(query.skip);
        }
        if (query && query.take !== undefined && query.take !== null) {
            request.setTake(query.take);
        }

        const response = await this._callAsync(() => this._mgmt.listDocuments(request));
        return response.getDocumentsList().map(p => this._toDocumentInfo(p));
    }

    async getDocumentInfoAsync(documentId) {
        const request = new this.Messages.MgmtDocumentRequest();
        request.setDocumentId(documentId);

        const response = await this._callAsync(() => this._mgmt.getDocumentInfo(request));
        return response.getFound() ? this._toDocumentInfo(response.getDocumentInfo()) : null;
    }

    async getSnapshotAsync(documentId) {
        const request = new this.Messages.MgmtDocumentRequest();
        request.setDocumentId(documentId);

        const response = await this._callAsync(() => this._mgmt.getSnapshot(request));
        return response.getFound() ? this._toSnapshot(response.getSnapshot()) : null;
    }

    async listMilestonesAsync(documentId) {
        const request = new this.Messages.MgmtDocumentRequest();
        request.setDocumentId(documentId);

        const response = await this._callAsync(() => this._mgmt.listMilestones(request));
        return response.getMilestonesList().map(m => ({
            revision: m.getRevision(),
            timestamp: this._toDateTime(m.getTimestamp()),
            name: m.getName()
        }));
    }

    async deleteDocumentAsync(documentId) {
        const request = new this.Messages.MgmtDocumentRequest();
        request.setDocumentId(documentId);
        await this._callAsync(() => this._mgmt.deleteDocument(request));
    }

    async compactDocumentAsync(documentId, upToRevision) {
        const request = new this.Messages.MgmtRevisionRequest();
        request.setDocumentId(documentId);
        request.setUpToRevision(upToRevision);
        await this._callAsync(() => this._mgmt.compactDocument(request));
    }

    async purgeHistoryAsync(documentId, upToRevision) {
        const request = new this.Messages.MgmtRevisionRequest();
        request.setDocumentId(documentId);
        request.setUpToRevision(upToRevision);
        await this._callAsync(() => this._mgmt.purgeHistory(request));
    }

    async purgeTenantAsync() {
        const request = new this.Messages.MgmtEmptyRequest();
        const response = await this._callAsync(() => this._mgmt.purgeTenant(request));
        return response.getCount();
    }

    // ── Names ─────────────────────────────────────────────────────────────────

    async listNamesAsync() {
        const request = new this.Messages.VerEmptyRequest();
        const response = await this._callAsync(() => this._ver.listNames(request));
        return response.getNamesList().map(p => this._toNameInfo(p));
    }

    async deleteNameAsync(name, cascade = false) {
        const request = new this.Messages.VerDeleteNameRequest();
        request.setName(name);
        request.setCascade(cascade);
        await this._callAsync(() => this._ver.deleteName(request));
    }

    // ── Branches ─────────────────────────────────────────────────────────────

    async listBranchesAsync(name) {
        const request = new this.Messages.VerNameRequest();
        request.setName(name);
        const response = await this._callAsync(() => this._ver.listBranches(request));
        return response.getBranchesList().map(p => this._toBranchRef(p));
    }

    async forkBranchAsync(name, fromBranchId, newBranchId, atRevision = null) {
        const request = new this.Messages.VerForkBranchRequest();
        request.setName(name);
        request.setFromBranchId(fromBranchId);
        request.setNewBranchId(newBranchId);
        
        if (atRevision !== null && atRevision !== undefined) {
            // Using setAtRevision assuming a primitive or a protobuf wrapper method is generated.
            request.setAtRevision(atRevision);
        }

        const response = await this._callAsync(() => this._ver.forkBranch(request));
        return this._toBranchRef(response.getBranch());
    }

    async deleteBranchAsync(name, branchId) {
        const request = new this.Messages.VerBranchRequest();
        request.setName(name);
        request.setBranchId(branchId);
        await this._callAsync(() => this._ver.deleteBranch(request));
    }

    // ── Versions / tags ───────────────────────────────────────────────────────

    async listVersionsAsync(name, branchId) {
        const request = new this.Messages.VerBranchRequest();
        request.setName(name);
        request.setBranchId(branchId);
        const response = await this._callAsync(() => this._ver.listVersions(request));
        return response.getVersionsList().map(p => this._toVersionRef(p));
    }

    async createVersionAsync(name, branchId, tag) {
        const request = new this.Messages.VerCreateVersionRequest();
        request.setName(name);
        request.setBranchId(branchId);
        request.setTag(tag);
        const response = await this._callAsync(() => this._ver.createVersion(request));
        return this._toVersionRef(response.getVersion());
    }

    async readVersionSnapshotAsync(name, branchId, tag) {
        const request = new this.Messages.VerVersionRequest();
        request.setName(name);
        request.setBranchId(branchId);
        request.setTag(tag);
        const response = await this._callAsync(() => this._ver.readVersionSnapshot(request));
        return response.getFound() ? this._toSnapshot(response.getSnapshot()) : null;
    }

    async deleteVersionAsync(name, branchId, tag, dropSnapshot = false) {
        const request = new this.Messages.VerDeleteVersionRequest();
        request.setName(name);
        request.setBranchId(branchId);
        request.setTag(tag);
        request.setDropSnapshot(dropSnapshot);
        await this._callAsync(() => this._ver.deleteVersion(request));
    }

    // ── Merge ─────────────────────────────────────────────────────────────────

    async mergeBranchAsync(name, targetBranchId, sourceBranchId, dryRun = false) {
        const request = new this.Messages.VerMergeRequest();
        request.setName(name);
        request.setTargetBranchId(targetBranchId);
        request.setSourceBranchId(sourceBranchId);
        request.setDryRun(dryRun);

        const response = await this._callAsync(() => this._ver.mergeBranch(request));
        const r = response.getMergeReport();
        return {
            sourceBranchId: r.getSourceBranchId(),
            targetBranchId: r.getTargetBranchId(),
            rebasedOpCount: r.getRebasedOpCount(),
            nullifiedOpCount: r.getNullifiedOpCount(),
            isDryRun: r.getIsDryRun()
        };
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    async disposeAsync() {
        // gRPC-Web clients do not typically need explicit disposal
        return Promise.resolve();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    async _callAsync(callFunc) {
        try {
            return await callFunc();
        } catch (ex) {
            // Equivalent to throwing OpStreamManagementException in C#
            // Code 7 corresponds to grpc.StatusCode.PERMISSION_DENIED
            const error = new Error(ex.message || ex.details || "gRPC error");
            error.isForbidden = ex.code === 7 || (ex.message && ex.message.includes("Forbidden"));
            error.innerException = ex;
            throw error;
        }
    }

    _toDateTime(protoTimestamp) {
        if (!protoTimestamp) return null;
        if (typeof protoTimestamp.toDate === 'function') {
            return protoTimestamp.toDate(); // google.protobuf.Timestamp usually has toDate()
        }
        // Fallback for custom implementation
        const secs = protoTimestamp.getSeconds ? protoTimestamp.getSeconds() : protoTimestamp.seconds || 0;
        const nanos = protoTimestamp.getNanos ? protoTimestamp.getNanos() : protoTimestamp.nanos || 0;
        return new Date((secs * 1000) + (nanos / 1000000));
    }

    _toDocumentInfo(p) {
        return {
            documentId: p.getDocumentId(),
            revision: p.getRevision(),
            lastModified: this._toDateTime(p.getLastModified()),
            opCount: p.getOpCount()
        };
    }

    _toSnapshot(p) {
        return {
            revision: p.getRevision(),
            timestamp: this._toDateTime(p.getTimestamp()),
            memory: p.getState()?.getMemory() || new Uint8Array() // Assuming memory is returned as Uint8Array
        };
    }

    _toNameInfo(p) {
        return {
            name: p.getName(),
            defaultBranchId: p.getDefaultBranchId(),
            engineType: p.getEngineType(),
            createdAt: this._toDateTime(p.getCreatedAt())
        };
    }

    _toBranchRef(p) {
        const forkParent = p.getForkParentBranchId();
        return {
            name: p.getName(),
            branchId: p.getBranchId(),
            physicalDocumentId: p.getPhysicalDocumentId(),
            forkParentBranchId: forkParent || null,
            forkRevision: p.getForkRevision(),
            createdAt: this._toDateTime(p.getCreatedAt()),
            isReadOnly: p.getIsReadOnly()
        };
    }

    _toVersionRef(p) {
        return {
            name: p.getName(),
            branchId: p.getBranchId(),
            tag: p.getTag(),
            revision: p.getRevision(),
            historySnapshotName: p.getHistorySnapshotName(),
            createdAt: this._toDateTime(p.getCreatedAt())
        };
    }
}
