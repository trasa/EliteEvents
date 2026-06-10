import { NextResponse } from "next/server";
import { getRedis } from "@/lib/redis";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

// Lightweight health check for an uptime monitor: 200 only if Redis answers a
// PING, 503 otherwise. No caching so each hit reflects current connectivity.
export async function GET() {
  try {
    const pong = await getRedis().ping();
    if (pong !== "PONG") {
      throw new Error(`unexpected PING reply: ${pong}`);
    }
    return NextResponse.json(
      { status: "ok", redis: "ok" },
      { status: 200, headers: { "Cache-Control": "no-store" } },
    );
  } catch (err) {
    console.error("health check failed", err);
    return NextResponse.json(
      { status: "error", redis: "unavailable" },
      { status: 503, headers: { "Cache-Control": "no-store" } },
    );
  }
}