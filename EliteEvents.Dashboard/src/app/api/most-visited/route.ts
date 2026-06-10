import { NextResponse } from "next/server";
import { getMostVisited } from "@/lib/queries";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

export async function GET(request: Request) {
  const { searchParams } = new URL(request.url);
  const limit = Math.min(Number(searchParams.get("limit") ?? 25) || 25, 100);
  try {
    const systems = await getMostVisited(limit);
    return NextResponse.json({ systems });
  } catch (err) {
    console.error("most-visited error", err);
    return NextResponse.json({ error: "redis unavailable" }, { status: 503 });
  }
}
