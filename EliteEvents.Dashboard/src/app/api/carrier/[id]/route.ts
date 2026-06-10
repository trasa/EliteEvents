import { NextResponse } from "next/server";
import { getCarrierActivity } from "@/lib/queries";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

export async function GET(
  _request: Request,
  { params }: { params: Promise<{ id: string }> },
) {
  const { id } = await params;
  try {
    const days = await getCarrierActivity(decodeURIComponent(id));
    return NextResponse.json({ carrier: id.toUpperCase(), days });
  } catch (err) {
    console.error("carrier error", err);
    return NextResponse.json({ error: "redis unavailable" }, { status: 503 });
  }
}
