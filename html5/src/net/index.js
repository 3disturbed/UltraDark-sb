// -----------------------------------------------------------------------------
// Networking — the browser half of a wire the native engine also speaks.
// -----------------------------------------------------------------------------

export {
    NetMessage, NET_PROTOCOL_VERSION, NetDelivery,
    NetWriter, NetReader, NetProtocolError, encodeJson, decodeJson,
} from './protocol.js';
export { NetworkManager, NetTransportKind } from './NetworkManager.js';
export { NetworkObject, ReplicateCondition } from './NetworkObject.js';
export { LoopbackTransport, NetPeerHandle } from './LoopbackTransport.js';
export { WebSocketTransport } from './WebSocketTransport.js';
export {
    createRoom, readRoom, roomSocketUrl, roomCodeFromUrl,
    generateRoomCode, isRoomCode, ROOM_CODE_ALPHABET, ROOM_CODE_LENGTH,
} from './rooms.js';
