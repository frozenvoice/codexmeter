(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory(require("./canonical.js"));
  } else {
    root.ProMeterChunk = factory(root.ProMeterCanonical);
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function (canonical) {
  var CHUNKABLE = {
    GetConversationHead: true,
    GetConversationFull: true,
    GetConversationLegacy: true,
    GetOlderConversationMessages: true
  };

  function isChunkableOperation(operation) {
    return !!CHUNKABLE[operation];
  }

  function bytesToBase64(bytes) {
    var binary = "";
    var chunk = 0x8000;
    for (var i = 0; i < bytes.length; i += chunk) {
      binary += String.fromCharCode.apply(null, bytes.subarray(i, Math.min(i + chunk, bytes.length)));
    }
    return btoa(binary);
  }

  function base64ToBytes(text) {
    var binary = atob(text);
    var bytes = new Uint8Array(binary.length);
    for (var i = 0; i < binary.length; i++) {
      bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
  }

  function splitBodyToBase64Chunks(bodyText, chunkRawBytes) {
    var bytes = new TextEncoder().encode(String(bodyText));
    var size = chunkRawBytes || canonical.CHUNK_RAW_BYTES;
    var chunks = [];
    for (var offset = 0; offset < bytes.length; offset += size) {
      chunks.push(bytesToBase64(bytes.subarray(offset, Math.min(offset + size, bytes.length))));
    }
    return { chunks: chunks, totalProjectedBytes: bytes.length };
  }

  function frameUtf8Length(frame) {
    return canonical.utf8ByteLength(JSON.stringify(frame));
  }

  function payloadTooLargeResult(requestId, operation) {
    return {
      ok: false,
      error: "PayloadTooLarge",
      messages: [
        {
          type: "invokeResult",
          requestId: requestId,
          operation: operation,
          status: 0,
          payloadTooLarge: true,
          error: "PayloadTooLarge",
          schemaMismatch: false
        }
      ]
    };
  }

  function buildFrames(requestId, operation, payload) {
    var body = payload && typeof payload.body === "string" ? payload.body : "";
    if (!isChunkableOperation(operation) || !body) {
      return payloadTooLargeResult(requestId, operation);
    }

    var split = splitBodyToBase64Chunks(body, canonical.CHUNK_RAW_BYTES);
    if (split.totalProjectedBytes > canonical.MAX_ASSEMBLED_PROJECTED_BYTES
        || split.chunks.length === 0
        || split.chunks.length > canonical.MAX_CHUNK_COUNT) {
      return payloadTooLargeResult(requestId, operation);
    }

    var start = {
      type: "invokeResultStart",
      requestId: requestId,
      operation: operation,
      chunked: true,
      chunkCount: split.chunks.length,
      totalProjectedBytes: split.totalProjectedBytes,
      status: payload.status,
      retryAfter: payload.retryAfter,
      error: payload.error,
      schemaMismatch: payload.schemaMismatch === true
    };
    var messages = [start];
    for (var i = 0; i < split.chunks.length; i++) {
      messages.push({
        type: "invokeResultChunk",
        requestId: requestId,
        operation: operation,
        chunkIndex: i,
        chunkCount: split.chunks.length,
        data: split.chunks[i]
      });
    }
    messages.push({
      type: "invokeResultEnd",
      requestId: requestId,
      operation: operation
    });

    for (var f = 0; f < messages.length; f++) {
      var size = frameUtf8Length(messages[f]);
      if (size > canonical.MAX_CHUNK_FRAME_BYTES || size > canonical.MAX_NATIVE_MESSAGE_BYTES) {
        return payloadTooLargeResult(requestId, operation);
      }
    }

    return {
      ok: true,
      chunkCount: split.chunks.length,
      totalProjectedBytes: split.totalProjectedBytes,
      messages: messages
    };
  }

  function reassembleBody(frames) {
    if (!Array.isArray(frames) || frames.length === 0) {
      return { ok: false, error: "chunk protocol error" };
    }
    var start = null;
    var end = null;
    var byIndex = {};
    for (var i = 0; i < frames.length; i++) {
      var frame = frames[i];
      if (!frame || typeof frame !== "object") {
        continue;
      }
      if (frame.type === "invokeResultStart") {
        start = frame;
      } else if (frame.type === "invokeResultEnd") {
        end = frame;
      } else if (frame.type === "invokeResultChunk") {
        if (typeof frame.chunkIndex !== "number" || frame.chunkIndex < 0) {
          return { ok: false, error: "chunk protocol error" };
        }
        if (Object.prototype.hasOwnProperty.call(byIndex, frame.chunkIndex)) {
          return { ok: false, error: "chunk protocol error" };
        }
        byIndex[frame.chunkIndex] = frame;
      }
    }
    if (!start || !end || typeof start.chunkCount !== "number" || start.chunkCount < 1) {
      return { ok: false, error: "chunk protocol error" };
    }
    if (typeof start.totalProjectedBytes === "number"
        && start.totalProjectedBytes > canonical.MAX_ASSEMBLED_PROJECTED_BYTES) {
      return { ok: false, error: "PayloadTooLarge" };
    }
    var parts = [];
    var total = 0;
    for (var index = 0; index < start.chunkCount; index++) {
      var chunk = byIndex[index];
      if (!chunk || typeof chunk.data !== "string") {
        return { ok: false, error: "chunk protocol error" };
      }
      var bytes = base64ToBytes(chunk.data);
      parts.push(bytes);
      total += bytes.length;
    }
    if (typeof start.totalProjectedBytes === "number" && total !== start.totalProjectedBytes) {
      return { ok: false, error: "chunk protocol error" };
    }
    if (total > canonical.MAX_ASSEMBLED_PROJECTED_BYTES) {
      return { ok: false, error: "PayloadTooLarge" };
    }
    var assembled = new Uint8Array(total);
    var offset = 0;
    for (var p = 0; p < parts.length; p++) {
      assembled.set(parts[p], offset);
      offset += parts[p].length;
    }
    return { ok: true, body: new TextDecoder().decode(assembled), totalProjectedBytes: total };
  }

  return {
    isChunkableOperation: isChunkableOperation,
    splitBodyToBase64Chunks: splitBodyToBase64Chunks,
    buildFrames: buildFrames,
    reassembleBody: reassembleBody,
    frameUtf8Length: frameUtf8Length
  };
});
