import Redis from "ioredis";

const REDIS_URL = process.env.REDIS_URL ?? "redis://localhost:6379";

// The channel your C# handler publishes live events to (see README).
export const EVENTS_CHANNEL = "eddn:events";

// Reuse one client across hot-reloads in dev so we don't leak connections.
const globalForRedis = globalThis as unknown as { redis?: Redis };

export function getRedis(): Redis {
  if (!globalForRedis.redis) {
    globalForRedis.redis = new Redis(REDIS_URL, { maxRetriesPerRequest: 3 });
  }
  return globalForRedis.redis;
}

// Pub/sub needs a dedicated connection — a subscriber socket can't run normal commands.
export function createSubscriber(): Redis {
  return new Redis(REDIS_URL, { maxRetriesPerRequest: null });
}
