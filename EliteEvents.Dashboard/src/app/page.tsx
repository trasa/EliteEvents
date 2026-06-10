import Leaderboard from "./components/Leaderboard";
import LiveTicker from "./components/LiveTicker";

export default function Home() {
  return (
    <div className="wrap">
      <header className="site">
        <h1>EDDN Live</h1>
        <p>
          Real-time galactic activity from the Elite Dangerous Data Network &mdash;
          ingested by a C# / NetMQ service, served here from Redis.
        </p>
      </header>

      <div className="grid">
        <Leaderboard />
        <LiveTicker />
      </div>
    </div>
  );
}
