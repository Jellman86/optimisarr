import Testing

// BSD-name reuse is a physical assertion. Independent live fixtures must not compete
// for the released minor while one of these tests is proving reuse of that exact name.
@Suite("Live RAM allocation qualification", .serialized)
struct LiveRamDiskTestSuite {}
