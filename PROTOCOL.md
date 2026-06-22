# Wire protocol

The agent sends one HTTP request per notification:

```
POST /notify HTTP/1.1
Content-Type: application/json
X-Bridge-Token: <token>        # only when a token is configured

{
  "id": "42",                  # guest-side notification id, used for dedup
  "app": "WeChat",             # source app display name
  "title": "Alice",            # first toast text line
  "body": "see you at 6pm",    # remaining toast text lines, '\n' joined
  "timestamp": "2026-06-22T10:15:30.0000000+08:00"   # ISO-8601, informational
}
```

All string fields are optional and default to empty. A request whose `title`
and `body` are both empty is dropped by the agent before sending.

## Responses

| status | meaning |
| --- | --- |
| `204 No Content` | accepted and shown (or intentionally suppressed) |
| `400 Bad Request` | body was not valid JSON for this schema |
| `401 Unauthorized` | token required but missing/wrong |
| `405 Method Not Allowed` | not a `POST` to `/notify` |

`GET /health` returns `200 OK` with body `ok` for liveness checks.

## Dedup

Both ends dedup by `id`:

- The agent records ids it has already forwarded and only sends newly added
  Action Center entries (it primes its seen-set on startup so it does not replay
  the existing backlog).
- The host keeps a small ring of recent ids and ignores repeats, which absorbs
  duplicates caused by agent restarts.
