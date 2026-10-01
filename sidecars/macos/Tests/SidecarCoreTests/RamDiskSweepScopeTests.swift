import Testing
@testable import SidecarCore

@Suite("RAM sweep ownership")
struct RamDiskSweepScopeTests {
    @Test("a live test selects only its own RAM volume")
    func ownedVolume() {
        #expect(RamDisk.strayVolumeNames(in: ["OptimisarrWork-test", "OptimisarrWork-other", "External"],
            ownedVolumeNames: ["OptimisarrWork-test"]) == ["OptimisarrWork-test"])
    }

    @Test("an explicit empty ownership set cannot sweep another worker")
    func emptyOwnership() {
        #expect(RamDisk.strayVolumeNames(in: ["OptimisarrWork-other"], ownedVolumeNames: []).isEmpty)
    }

    @Test("startup cleanup retains its existing prefix boundary")
    func startupScope() {
        #expect(RamDisk.strayVolumeNames(in: ["External", "OptimisarrWork-old"], ownedVolumeNames: nil)
            == ["OptimisarrWork-old"])
    }
}
