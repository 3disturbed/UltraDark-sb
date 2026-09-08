---
paths:
  - "SexyBiscuit.Engine/Networking/**"
  - "SexyBiscuit.Engine/DarksGames/**"
  - "html5/src/net/**"
  - "html5/src/dg/**"
  - "html5/tools/roomserver.js"
---
# One wire, one contract, two engines

The frame table, message ids, replication tags and the Darks Games API are shared byte for byte
between `SexyBiscuit.Engine/Networking` + `DarksGames` and `html5/src/net` + `dg`. WebSocket is
the shared transport; LiteNetLib UDP is desktop-only behind the transport seam.

- Read `wiki/15-networking.md` and `wiki/29-darksgames.md` before changing either side.
- A new message or tag lands on both sides in one commit; `NetworkingTests` and `net.test.js`
  read the other side's source and fail otherwise.
- `node html5/tools/roomserver.js` is the relay a web build joins through;
  `html5/tests/roomserver.test.js` covers it.
