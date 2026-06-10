export interface VisitedSystem {
  system: string;
  visits: number;
}

export interface StationDocking {
  station: string;
  type: string;
  count: number;
  lastSeen: number; // unix seconds
}

export interface CarrierDay {
  date: string;
  dockings: number;
}

// Shape of messages your C# handler publishes to the eddn:events channel.
export interface LiveEvent {
  type: string; // "docked" | "fsdjump" | ...
  system?: string;
  station?: string;
  stationType?: string;
  ts: number; // unix seconds
}
