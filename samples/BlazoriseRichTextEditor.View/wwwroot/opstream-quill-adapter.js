// opstream-quill-adapter.js
//
// Bridges Blazorise's Quill instance with OpStream's rich-text engine.
//
// Wire format expected by OpStream.Server.Engine.RichText.RichTextEngine:
//   { components: [ { "$type":"insert", text, attributes?, },
//                   { "$type":"retain", count, attributes? },
//                   { "$type":"delete", count } ] }
//
// Quill's "text-change" event hands us a Delta that is almost identical in
// shape — we just translate keys: `insert→text/$type`, `retain→count/$type`,
// `delete→count/$type`. The reverse translation lets us apply remote ops via
// quill.updateContents(delta, "silent").

window.OpStreamQuill = (function () {
    const adapters = new Map(); // elementId -> adapter

    function findQuillInstance( wrapperEl ) {
        if ( !wrapperEl ) return null;
        // Blazorise mounts Quill on the .b-richtextedit-editor child and stashes
        // the instance at editorRef.quill (see Blazorise.RichTextEdit's richtextedit.js).
        const editorRef = wrapperEl.getElementsByClassName( "b-richtextedit-editor" )[0];
        return editorRef && editorRef.quill ? editorRef.quill : null;
    }

    function waitForQuill( elementId, attempts ) {
        return new Promise( ( resolve, reject ) => {
            const tick = ( remaining ) => {
                const el = document.getElementById( elementId );
                const quill = findQuillInstance( el );
                if ( quill ) return resolve( quill );
                if ( remaining <= 0 ) return reject( new Error( "Quill instance not found for #" + elementId ) );
                setTimeout( () => tick( remaining - 1 ), 100 );
            };
            tick( attempts ?? 100 );
        } );
    }

    function quillDeltaToRichTextOp( delta ) {
        const components = [];
        for ( const op of delta.ops ) {
            if ( typeof op.insert === "string" ) {
                const c = { "$type": "insert", text: op.insert };
                if ( op.attributes ) c.attributes = op.attributes;
                components.push( c );
            } else if ( op.insert !== undefined ) {
                // Embed (image/video/etc.) — serialise as a single-character marker.
                // Not lossless, but lets the document stay consistent for the demo.
                const c = { "$type": "insert", text: "￼" };
                if ( op.attributes ) c.attributes = op.attributes;
                components.push( c );
            } else if ( typeof op.retain === "number" ) {
                const c = { "$type": "retain", count: op.retain };
                if ( op.attributes ) c.attributes = op.attributes;
                components.push( c );
            } else if ( typeof op["delete"] === "number" ) {
                components.push( { "$type": "delete", count: op["delete"] } );
            }
        }
        return { components };
    }

    function richTextOpToQuillDelta( op ) {
        const ops = [];
        for ( const c of op.components || [] ) {
            const kind = c["$type"] || c.type;
            if ( kind === "insert" ) {
                const o = { insert: c.text ?? "" };
                if ( c.attributes ) o.attributes = c.attributes;
                ops.push( o );
            } else if ( kind === "retain" ) {
                const o = { retain: c.count ?? 0 };
                if ( c.attributes ) o.attributes = c.attributes;
                ops.push( o );
            } else if ( kind === "delete" ) {
                ops.push( { "delete": c.count ?? 0 } );
            }
        }
        return { ops };
    }

    function richTextDocumentToQuillDelta( doc ) {
        // Snapshot shape is { content: [ { text, attributes? } ] } where each
        // element is an Insert (the engine's RichTextDocument.Content).
        const ops = [];
        for ( const seg of ( doc && doc.content ) || [] ) {
            const o = { insert: seg.text ?? "" };
            if ( seg.attributes ) o.attributes = seg.attributes;
            ops.push( o );
        }
        return { ops };
    }

    return {
        attach: async function ( elementId, dotnetRef ) {
            const quill = await waitForQuill( elementId, 200 );

            // FIFO outbox so concurrent local edits stay in order on the wire.
            const outbox = [];
            let flushing = false;
            let remoteApplyDepth = 0;

            const flush = async () => {
                if ( flushing ) return;
                flushing = true;
                try {
                    while ( outbox.length > 0 ) {
                        const opJson = outbox.shift();
                        try {
                            await dotnetRef.invokeMethodAsync( "OnLocalOp", opJson );
                        } catch ( err ) {
                            console.error( "[OpStreamQuill] OnLocalOp failed:", err );
                            break;
                        }
                    }
                } finally {
                    flushing = false;
                }
            };

            const onTextChange = function ( delta, oldDelta, source ) {
                if ( source !== "user" ) return;
                if ( remoteApplyDepth > 0 ) return;

                const richOp = quillDeltaToRichTextOp( delta );
                if ( richOp.components.length === 0 ) return;

                outbox.push( JSON.stringify( richOp ) );
                flush();
            };

            const onSelectionChange = function ( range, oldRange, source ) {
                if ( source !== "user" ) return;
                if ( !range ) return;
                dotnetRef.invokeMethodAsync( "OnSelectionChanged", range.index, range.length )
                    .catch( e => console.warn( "[OpStreamQuill] OnSelectionChanged:", e ) );
            };

            quill.on( "text-change", onTextChange );
            quill.on( "selection-change", onSelectionChange );

            adapters.set( elementId, {
                quill,
                detach: () => {
                    quill.off( "text-change", onTextChange );
                    quill.off( "selection-change", onSelectionChange );
                    adapters.delete( elementId );
                },
                applyRemoteOp: ( opJson ) => {
                    const delta = richTextOpToQuillDelta( JSON.parse( opJson ) );
                    remoteApplyDepth++;
                    try {
                        quill.updateContents( delta, "silent" );
                    } finally {
                        remoteApplyDepth--;
                    }
                },
                setSnapshot: ( snapshotJson ) => {
                    if ( !snapshotJson ) return 0;
                    const doc = JSON.parse( snapshotJson );
                    const delta = richTextDocumentToQuillDelta( doc );
                    remoteApplyDepth++;
                    try {
                        quill.setContents( delta, "silent" );
                    } finally {
                        remoteApplyDepth--;
                    }
                    return quill.getLength();
                },
                getSelection: () => {
                    const r = quill.getSelection();
                    return r ? { index: r.index, length: r.length } : { index: 0, length: 0 };
                },
                getLength: () => quill.getLength(),
            } );

            return true;
        },

        applyRemoteOp: function ( elementId, opJson ) {
            const a = adapters.get( elementId );
            if ( a ) a.applyRemoteOp( opJson );
        },

        setSnapshot: function ( elementId, snapshotJson ) {
            const a = adapters.get( elementId );
            return a ? a.setSnapshot( snapshotJson ) : 0;
        },

        getSelection: function ( elementId ) {
            const a = adapters.get( elementId );
            return a ? a.getSelection() : { index: 0, length: 0 };
        },

        getLength: function ( elementId ) {
            const a = adapters.get( elementId );
            return a ? a.getLength() : 0;
        },

        detach: function ( elementId ) {
            const a = adapters.get( elementId );
            if ( a ) a.detach();
        },
    };
} )();
