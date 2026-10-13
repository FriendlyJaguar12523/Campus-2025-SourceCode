// ───── CAMPUS — central demo data ─────
// All mock/seed data lives here so it's easy to find and edit in one place.
// app.js reads from window.CAMPUS_DATA (with inline fallbacks if this file is missing).
window.CAMPUS_DATA = {
  // The site is now served BY the Campus C# server (Kestrel), not by the old Flask
  // server.py, so every backend call is same-origin and apiBase stays empty.
  apiBase: '',

  // apiBase '' is falsy, and the old code used `if (!apiBase)` as its "is the API
  // available?" test - which meant a same-origin deployment silently ran in demo mode
  // with search and login disabled. This flag is the real switch; set it to false to
  // go back to the offline demo data.
  apiEnabled: true,

  // Image filenames (ProfileImage / ImageName) are served directly by the C# server's
  // ImagesController at /img/. The old /api/img/ Flask proxy (which fetched from an
  // external, Cloudflare-protected host using a stored token) is gone.
  imgBase: '/img/',

  // featured / popular rooms
  rooms: {
    'whisker-heights': { name: 'Whisker Heights', creator: 'clawful',     a: '#ff5a3c', b: '#ffb142', players: 2143, visits: '67k', desc: 'Scale a floating city of rooftops and neon. Co-op parkour for up to 16 cats.' },
    'midnight-mews':   { name: 'Midnight Mews',   creator: 'velvetpaw',   a: '#f0453f', b: '#ff8a5c', players: 312,  visits: '31k', desc: 'A cozy late-night hangout under the stars — chill, chat, and build with friends.' },
    'litterbox-lab':   { name: 'Litterbox Lab',   creator: 'tinker9',     a: '#ff6b9d', b: '#ffc371', players: 540,  visits: '22k', desc: 'Tinker with wild contraptions and machines in this maker playground.' },
    'yarn-quest':      { name: 'Yarn Quest',      creator: 'stitchwitch', a: '#e84393', b: '#ff7f6e', players: 880,  visits: '67k', desc: 'An epic co-op adventure through a hand-stitched world of yarn and wool.' },
    'deep-dive-reef':  { name: 'Deep Dive Reef',  creator: 'bubbles',     a: '#c44569', b: '#ff7f50', players: 198,  visits: '19k', desc: 'Explore a glowing underwater reef and discover its hidden depths.' },
    'haunted-cattery': { name: 'Haunted Cattery', creator: 'spookytom',   a: '#d63031', b: '#ff7675', players: 1500, visits: '91k', desc: 'A spooky mansion full of jump-scares, puzzles, and secrets to uncover.' },
  },

  // notification dropdown items — empty by default (no notifications when logged in)
  notifications: [],

  // updates page content — Patch Notes / Weekly / News (edit freely)
  updates: {
    patch: {
      title: 'Patch Notes',
      blurb: 'Every fix, tweak and balance change, newest first.',
      posts: [
        { date: '2026-06-16', tag: 'v1.4.2', title: 'Stability & login fixes', body: 'Fixed a crash when joining full rooms, improved login reliability on mobile, and reduced load times in the Rec Center.' },
        { date: '2026-06-09', tag: 'v1.4.1', title: 'Photo upload improvements', body: 'Photos now upload faster and keep their full resolution. Fixed profile pictures occasionally not updating.' },
        { date: '2026-06-02', tag: 'v1.4.0', title: 'New creator tools', body: 'Added the maker pen radial menu, more room tags, and a fresh set of props for builders.' },
      ],
    },
    weekly: {
      title: 'Weekly',
      blurb: 'Your weekly roundup of featured rooms, events and creators.',
      posts: [
        { date: '2026-06-15', tag: 'Week 24', title: 'Featured: Whisker Heights', body: 'This week we are spotlighting Whisker Heights by @clawful — a co-op parkour city with neon rooftops. Jump in this weekend for double cheers!' },
        { date: '2026-06-08', tag: 'Week 23', title: 'Creator of the week: @stitchwitch', body: 'Yarn Quest hit 67k visits. We sat down with @stitchwitch to talk hand-stitched worlds and what is next.' },
      ],
    },
    news: {
      title: 'News',
      blurb: 'Announcements and what is happening across Campus.',
      posts: [
        { date: '2026-06-14', tag: 'Event', title: 'Summer Paw Festival incoming', body: 'Our biggest seasonal event returns at the end of the month — limited-time rooms, prizes and a community build challenge.' },
        { date: '2026-06-10', tag: 'Update', title: 'Mobile cross-play is here', body: 'You can now hang out across PC, Quest and mobile in the same rooms. Bring your friends on any device.' },
      ],
    },
  },

  // "online now" friends shown on the home rail
  onlineFriends: [
    { h: 'velvetpaw',   where: 'Midnight Mews' },
    { h: 'clawful',     where: 'Whisker Heights' },
    { h: 'stitchwitch', where: 'Yarn Quest' },
    { h: 'bubbles',     where: 'In the menu' },
  ],
};