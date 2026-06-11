import Leaderboard from "./components/Leaderboard";
import LiveTicker from "./components/LiveTicker";

export default function Home() {
  return (
    <div className="wrap">
      <header className="site">
        <h1>EDDN Live</h1>
        <p>
          Real-time galactic activity from the Elite Dangerous Data Network &mdash;
          ingested by a <a href="https://elite-visitors.meancat.com" target="_blank" rel="noopener noreferrer">C# / NetMQ service</a>, served here from Redis.
        </p>
      </header>

      <div className="grid">
        <Leaderboard />
        <LiveTicker />
      </div>

      <footer className="site-footer">
        <a href="https://github.com/trasa" target="_blank" rel="noopener noreferrer">
          GitHub
        </a>
        <span aria-hidden="true">&middot;</span>
        <a href="https://www.linkedin.com/in/tonyrasa/" target="_blank" rel="noopener noreferrer">
          LinkedIn
        </a>
      </footer>
    </div>
  );
}
