import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "EDDN Live — Elite Dangerous Galactic Activity",
  description:
    "Live galactic activity from the Elite Dangerous Data Network — most-visited systems, station docking, and a real-time event feed.",
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
