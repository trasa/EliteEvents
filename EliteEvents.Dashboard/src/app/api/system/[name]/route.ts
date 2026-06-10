import { NextResponse } from "next/server";
import { getSystemStations } from "@/lib/queries";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

export async function GET(
  _request: Request,
  { params }: { params: Promise<{ name: string }> },
) {
  const { name } = await params;
  try {
    const stations = await getSystemStations(decodeURIComponent(name));
    return NextResponse.json({ system: name.toUpperCase(), stations });
  } catch (err) {
    console.error("system error", err);
    return NextResponse.json({ error: "redis unavailable" }, { status: 503 });
  }
}
