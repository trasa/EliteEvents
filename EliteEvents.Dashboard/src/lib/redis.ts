import { readFileSync } from "node:fs";
import Redis, { type RedisOptions } from "ioredis";

// The channel your C# handler publishes live events to (see README).
export const EVENTS_CHANNEL = "eddn:events";

// Build a client.
//
// In production (Docker) the Redis password is mounted as a secret file pointed
// to by REDIS_AUTH_FILE — the same secret the C# EliteEvents.Visitors container
// uses — and the host/port/TLS come from env. This keeps the password out of the
// compose file and image. For local dev we fall back to a single REDIS_URL.
function createClient(extra: RedisOptions): Redis {
  const authFile = process.env.REDIS_AUTH_FILE;
  if (authFile) {
    return new Redis({
      host: process.env.REDIS_HOST ?? "localhost",
      port: Number(process.env.REDIS_PORT ?? "6379"),
      password: readFileSync(authFile, "utf8").trim(),
      tls: process.env.REDIS_TLS === "true" ? {} : undefined,
      ...extra,
    });
  }
  return new Redis(process.env.REDIS_URL ?? "redis://localhost:6379", extra);
}

// Reuse one client across hot-reloads in dev so we don't leak connections.
const globalForRedis = globalThis as unknown as { redis?: Redis };

export function getRedis(): Redis {
  if (!globalForRedis.redis) {
    globalForRedis.redis = createClient({ maxRetriesPerRequest: 3 });
  }
  return globalForRedis.redis;
}

// Pub/sub needs a dedicated connection — a subscriber socket can't run normal commands.
export function createSubscriber(): Redis {
  return createClient({ maxRetriesPerRequest: null });
}
