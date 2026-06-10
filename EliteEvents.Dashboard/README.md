# EliteEvents.Web

A Next.js + TypeScript dashboard for the EliteEvents EDDN pipeline. It reads the
**same Redis** that `EliteEvents.Visitors` / `DockingRedisService` already populates,
so it shares live state with the existing C# ingestion with no changes to the
ingestion logic.

It exists to show the Next.js / Node / TypeScript / React side of the stack on top
of the C# NetMQ + Redis backend I already run.

## What it does

- **Most-visited systems** leaderboard (reads the `systems:visits` sorted set), auto-refreshing client component using React hooks.
- **System detail** page (`/system/[NAME]`), a Server Component reading `system:{SYSTEM}:stations` + the per-station hashes directly.
- **Live feed** via Server-Sent Events (`/api/stream`), relaying a Redis pub/sub channel to the browser.
- REST route handlers: `/api/most-visited`, `/api/system/[name]`, `/api/carrier/[id]`.

## Redis keys consumed 

These are written by the C# event writer.

| Key | Type | Written by |
|-----|------|-----------|
| `systems:visits` | sorted set (member = SYSTEM, score = count) | `RecordSystemVisitAsync` |
| `system:{SYSTEM}:stations` | sorted set (member = station, score = last_seen) | `RecordStationDockingAsync` |
| `system:{SYSTEM}:station:{station}` | hash `{ count, type, last_seen }` | `RecordStationDockingAsync` |
| `carrier:{ID}:days` | sorted set (member = date, score = unix) | `RecordFleetCarrierDockingAsync` |
| `carrier:{ID}:daily:{date}` | string counter | `RecordFleetCarrierDockingAsync` |

System names and carrier IDs are upper-cased to match how the C# side stores them.

## Local dev

```bash
npm install
cp .env.example .env.local        # set REDIS_URL to your Redis
npm run dev                       # http://localhost:3000
```

## Notes

- Route handlers are `runtime = "nodejs"` + `dynamic = "force-dynamic"` because they
  hit Redis on every request (ioredis needs the Node runtime; no caching).
- The SSE endpoint opens one dedicated subscriber connection per client and cleans it
  up on disconnect. if it ever needs to fan out to many clients, we should switch to 
  one shared subscriber multiplexing across connected streams.
