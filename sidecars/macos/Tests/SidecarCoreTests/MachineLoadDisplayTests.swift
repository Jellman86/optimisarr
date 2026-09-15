import Foundation
import Testing
@testable import SidecarCore

struct MachineLoadDisplayTests {
    @Test("a reading that could not be taken leaves the last one standing")
    func holdsTheLastGoodFigure() {
        // The defect: the meters were assigned the sample directly, so every interval the kernel
        // could not answer for blanked them. At six samples a second that is a bar flickering in
        // and out, not a machine going idle.
        var display = MachineLoadDisplay()
        display.observe(MachineLoad(cpu: 0.4, gpu: 0.7))
        display.observe(nil)

        #expect(display.cpu == 0.4)
        #expect(display.gpu == 0.7)
    }

    @Test("each figure stands on its own")
    func figuresAreIndependent() {
        // A Mac can report its CPU while having no readable accelerator, and either can be
        // momentarily unmeasurable, so one missing figure must not disturb the other.
        var display = MachineLoadDisplay()
        display.observe(MachineLoad(cpu: 0.4, gpu: 0.7))
        display.observe(MachineLoad(cpu: 0.9, gpu: nil))

        #expect(display.cpu == 0.9)
        #expect(display.gpu == 0.7)
    }

    @Test("nothing to show until something has been read")
    func startsEmpty() {
        var display = MachineLoadDisplay()
        #expect(display.isEmpty)
        display.observe(nil)
        #expect(display.isEmpty)
        display.observe(MachineLoad(cpu: nil, gpu: nil))
        #expect(display.isEmpty)
    }

    @Test("the work stopping is what clears the meters")
    func clearing() {
        var display = MachineLoadDisplay()
        display.observe(MachineLoad(cpu: 0.4, gpu: 0.7))
        display.clear()

        #expect(display.isEmpty)
        // And a stale figure does not come back on the next failed reading.
        display.observe(nil)
        #expect(display.isEmpty)
    }

    @Test("a genuine zero is shown rather than swallowed")
    func zeroIsAFigure() {
        // Nil and zero mean different things: "cannot say" against "idle". Holding the last good
        // value must not turn an honest zero into the previous number.
        var display = MachineLoadDisplay()
        display.observe(MachineLoad(cpu: 0.9, gpu: 0.9))
        display.observe(MachineLoad(cpu: 0, gpu: 0))

        #expect(display.cpu == 0)
        #expect(display.gpu == 0)
    }
}
