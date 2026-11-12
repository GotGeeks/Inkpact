# Sync protocol

How a client and the server talk over the sync WebSocket. The code is the source: `server/src/Inkpact.Api/Sync/Frame.cs` and `web/src/sync/protocol.ts` hold the same format, and a change to one is a change to both and to this page. The design behind it is in `docs/architecture.md`, "Data model and sync design".

## The connection

- One WebSocket per client session, at `/api/sync`. Every message names its page, so one socket carries every page the tab has open.
- The request must be signed in (the session cookie) and must come from the app's own pages: the `Origin` header has to equal the scheme and `Host` the server sees. A request without an `Origin` is refused (`403 origin_invalid`).
- The client names its account in a WebSocket subprotocol, `inkpact.user.<user id>`. When that is not the account of the cookie (another tab signed in as someone else), the upgrade is refused with `409 session_changed`.
- Messages are binary. Numbers, strings and byte arrays use the lib0 encoding that Yjs uses: `varuint`, `varstring` (a `varuint` length and UTF-8 bytes) and `varUint8Array` (a `varuint` length and the bytes).

## Messages

A message is `[varuint type][varstring pageId][payload]`. The page id is a UUID in its canonical lower-case form with dashes; any other spelling is a protocol error, so one page never has two names. Ping and Pong are the type byte alone.

| Type | Name | Sent by | Payload |
| --- | --- | --- | --- |
| 0 | Subscribe | client | `varUint8Array`: the client's Yjs state vector, which may be empty |
| 1 | Unsubscribe | client | none |
| 2 | Update | both | `varUint8Array`: one Yjs update. The server stores it and relays it without decoding it |
| 3 | Sync | server | `varUint8Array` state vector, `varuint` count, then that many `varUint8Array` updates. The answer to Subscribe |
| 4 | Awareness | both | `varUint8Array`: a y-protocols awareness update (`varuint` count, then per entry `varuint` client id, `varuint` clock, `varstring` JSON state; the state `null` removes the entry). Relayed, never stored |
| 5 | Ping | client | none, and no page id: the one byte |
| 6 | Pong | server | none, and no page id: the one byte |
| 7 | Error | server | `varstring`: an error code about this page |
| 8 | Replaced | server | none: the Sync that follows replaces the page's whole content |

A client may send only Subscribe, Unsubscribe, Update, Awareness and Ping. Anything else, an unknown type or bytes after the payload closes the connection as a protocol error.

## Opening a page

1. The client sends Subscribe with its state vector.
2. The server checks the person's role on the page and answers with one Sync:
   - **Diff mode** (the usual case): the server's own state vector and one update, the diff the client is missing.
   - **Log mode** (the Yjs sidecar is down, or the page's stored updates are over `Sync:MaxDiffBytes`): an empty state vector and the stored updates in order. The client works out what the server lacks.
3. The client applies the updates and sends, as Update messages, what the server's state vector does not cover.
4. From then on both sides send Update messages as edits happen. The server stores an update before it relays it, so a peer never sees an update the log does not have.

The role is read when the page is subscribed. A viewer receives updates; an Update from a viewer is answered with the error `read_only` and not stored.

## When the server replaces a page's content

The server sometimes makes an update itself that deletes every block of a page and writes new content: a Joplin re-import of a note that changed there, and a restored version (run B.5b). It is stored under the page's lock, only when no client's update was stored since the page was read, and then sent to everyone who has the page open as two messages, in this order:

1. **Replaced**, with the page id.
2. A **Sync** with the server's state vector right after the replacement and the replacement as its one update.

It is a Sync and not an Update because a client checks a Sync and not an Update: before applying it, the client notes what it made that the server's state vector does not cover, and when the Sync deletes any of that text, it keeps the page as it was as a copy ([offline-copy.md](design-notes/offline-copy.md)). An update that was on its way when the page was replaced is stored after the replacement and is not in that state vector, so its text is found. A client that has the page open handles a Sync at any time, also one it did not ask for.

Replaced widens that check. A client that knows it keeps the copy when it has **any** change the server has not confirmed (text, formatting, a deletion), whatever the Sync deletes: the replacement was made without it. The server reads and answers a socket in order, so a change is unconfirmed exactly when it is not in the replacement. With nothing unconfirmed, the client sends nothing back after this Sync.

A client from before this message ignores a type it does not know, so a tab that was not reloaded still keeps text, but not a change that is only formatting or only a deletion. The same goes for a document that comes back from the browser's storage after the replacement: its Sync is an ordinary one. No text is lost in either case.

## Errors about one page

An Error message leaves the connection open.

| Code | Meaning |
| --- | --- |
| `page_not_found` | No such page, or no access to it. The two are not told apart, as in the REST API |
| `read_only` | The person may read the page but not change it. The update was not stored |
| `too_many_pages` | The connection already receives as many pages as it may (`Sync:MaxPagesPerConnection`) |

## Closing

The close reason is a code for the client to act on.

| Close code | Reason | When |
| --- | --- | --- |
| 4401 | `session_ended` | The session was logged out or has expired. The client goes to the login page and does not reconnect |
| 1008 (policy violation) | `protocol_error` | A message the server could not read or may not receive |
| 1008 (policy violation) | `rate_limited` | More messages than `Sync:MessagesPerSecond` allows after the burst |
| 1009 (message too big) | `message_too_big` | One message over `Sync:MaxMessageBytes` |
| 1011 (internal error) | `server_error` | A fault on the server |
| 1001 (endpoint unavailable) | `server_stopping` | The server is shutting down |

A client that falls too far behind (its outgoing queue passes `Sync:MaxQueuedMessages` or `Sync:MaxQueuedBytes`) is dropped without a close message. It reconnects and subscribes again, which brings it up to date.

## Keeping the connection alive

- The client sends Ping and gives up on a connection that stays silent, because a phone that loses its network never closes its socket.
- The server sends WebSocket pings on an idle socket (`Sync:KeepAliveInterval`) and closes one that does not answer in time (`Sync:KeepAliveTimeout`).
- Every `Sync:SessionCheckInterval` the server checks that the socket's session still exists. A logout closes the person's sockets at once.

## Limits

All in the `Sync` configuration section (`SyncOptions.cs`), with these defaults:

| Setting | Default | What it limits |
| --- | --- | --- |
| `MaxMessageBytes` | 2 MiB | One message from a client |
| `MessagesPerSecond`, `MessageBurst` | 100, 500 | Messages a connection may send |
| `MaxPagesPerConnection` | 100 | Pages one socket receives |
| `MaxConnectionsPerUser` | 20 | Open sockets per person |
| `MaxQueuedMessages`, `MaxQueuedBytes` | 1000, 16 MiB | What may wait to go out to one client |
| `MaxDiffBytes` | 2 MiB | Above this a page is sent as stored, without a diff |

The largest page that can be opened is half of `MaxQueuedBytes` (8 MiB), since a page is opened by one Sync that holds all of it.
