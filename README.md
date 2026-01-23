# Elite Dangerous Visitor Analytics

A real-time analytics platform for tracking commander docking events throughout the Elite Dangerous galaxy.

## Overview

This web application provides insights into station and fleet carrier traffic patterns by analyzing docking data from the Elite Dangerous Data Network (EDDN). Commanders can search for star systems or fleet carriers to view visitor statistics and activity trends.

### Features

- **System Search** - View docking statistics for all stations within a star system
- **Fleet Carrier Search** - Track day-by-day visitor counts for individual fleet carriers
- **Real-time Data** - Live updates via EDDN data feed
- **Automatic Data Retention** - 30-day rolling window with automatic purging of inactive entries

## Technology Stack

### Backend
- **ASP.NET Core Blazor Server** - Web framework
- **Redis** - Data storage and caching
- **ZeroMQ** - Message bus for EDDN data ingestion

### Frontend
- **Bootstrap 5** - CSS framework
- **Bootstrap Icons** - Icon library
- **Elite Dangerous Assets** - Custom fonts and imagery

## Data Source

Data is sourced from the [Elite Dangerous Data Network (EDDN)](https://github.com/EDCD/EDDN), a real-time feed of player-submitted journal data. Visit [EDDN Realtime](https://eddn-realtime.space/) to learn more about the network.

## Credits

### UI Framework
- [Bootstrap 5](https://getbootstrap.com/) - CSS Framework
- [Elite Dangerous Assets](https://edassets.org/) - Fonts and Images

### External Resources
- [Inara](https://inara.cz/) - Elite Dangerous Database & Community
- [Elite Dangerous Data Network (EDDN)](https://github.com/EDCD/EDDN) - Realtime Data Feed

## License

© 2026 [Tony Rasa](https://www.linkedin.com/in/tonyrasa/)

Not run by or affiliated in any way with [Frontier Developments plc](http://www.frontier.co.uk/)

---

*Elite Dangerous is a registered trademark of Frontier Developments plc.*
