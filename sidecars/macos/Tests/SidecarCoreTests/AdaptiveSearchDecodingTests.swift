import Foundation
import Testing
@testable import SidecarCore

/// Reading a search off the wire, against the two shapes a server has actually sent.
///
/// This is the test that was missing. The search was decoded with the strict contract decoder,
/// which required three fields the planner's contract does not carry, so a step that arrived
/// perfectly well formed became nil — and nil already meant "no search", which is an ordinary
/// thing for an assignment to say. Every searched job encoded at the library's baseline, no probe
/// was ever reported, and nothing anywhere said so.
struct AdaptiveSearchDecodingTests {
    /// The five-field contract the planner holds, as one server sent it.
    private var plannerShaped: [String: Any] {
        [
            "quality": 24,
            "sampleCommands": [["-i", "{{input}}", "{{output}}"]],
            "measurement": [
                "model": "vmaf_v0.6.1",
                "sampling": "Adaptive sample at quality 24",
                "minimumHarmonicMean": 85.0,
                "minimumMinimum": 70.0,
                "commands": [["-i", "{{distorted}}", "-i", "{{reference}}"]],
            ],
        ]
    }

    /// The eight-field gate the assignment's own quality carries, which is now sent for both.
    private var gateShaped: [String: Any] {
        var json = plannerShaped
        var measurement = json["measurement"] as! [String: Any]
        measurement["measure"] = true
        measurement["frameSubsample"] = 1
        measurement["clipVmaf"] = false
        json["measurement"] = measurement
        return json
    }

    @Test("a search in the shape the server now sends is read")
    func readsTheSharedShape() {
        let step = AdaptiveSearchStep(json: gateShaped)
        #expect(step?.quality == 24)
        #expect(step?.measurement.model == "vmaf_v0.6.1")
        #expect(step?.measurement.commands.count == 1)
        #expect(step?.measurement.minimumHarmonicMean == 85)
        #expect(step?.measurement.measure == true)
    }

    @Test("a search carrying only the facts a measurement needs is still read")
    func readsThePlannerShape() {
        // The shape that used to be dropped. `measure` is implied by being asked to measure, and
        // the other two are already baked into the commands, so their absence is not a reason to
        // throw a search away.
        let step = AdaptiveSearchStep(json: plannerShaped)
        #expect(step != nil)
        #expect(step?.quality == 24)
        #expect(step?.measurement.commands.count == 1)
        #expect(step?.measurement.measure == true)
    }

    @Test("an assignment carrying either shape ends up with a search to run")
    func assignmentKeepsTheSearch() {
        for search in [plannerShaped, gateShaped] {
            let assignment = Assignment(json: [
                "leaseId": UUID().uuidString,
                "jobId": 7,
                "sourceBytes": 4096,
                "videoEncoder": "hevc_videotoolbox",
                "renewWithinSeconds": 30,
                "arguments": ["-i", "{{input}}", "{{output}}"],
                "outputExtension": "mkv",
                "quality": [
                    "measure": true, "model": "vmaf_v0.6.1", "frameSubsample": 1,
                    "clipVmaf": false, "minimumHarmonicMean": 85.0, "minimumMinimum": 70.0,
                    "commands": [["-i", "{{distorted}}"]], "sampling": "Three windows",
                ],
                "search": search,
            ])
            #expect(assignment?.search != nil)
            #expect(assignment?.search?.quality == 24)
        }
    }

    @Test("an assignment with no search at all is still an assignment")
    func noSearchIsOrdinary() {
        let assignment = Assignment(json: [
            "leaseId": UUID().uuidString,
            "jobId": 7,
            "sourceBytes": 4096,
            "maxCandidateBytes": 4095,
            "videoEncoder": "hevc_videotoolbox",
            "renewWithinSeconds": 30,
            "arguments": ["-i", "{{input}}", "{{output}}"],
            "outputExtension": "mkv",
            "quality": [
                "measure": false, "model": "vmaf_v0.6.1", "frameSubsample": 1,
                "clipVmaf": false, "minimumHarmonicMean": 0.0, "minimumMinimum": 0.0,
            ],
        ])
        #expect(assignment != nil)
        #expect(assignment?.search == nil)
        #expect(assignment?.maxCandidateBytes == 4095)
    }

    @Test("a search that genuinely cannot be read is dropped, not guessed at")
    func refusesNonsense() {
        // Leniency has a floor: without commands there is nothing to measure with, and inventing
        // one would be worse than declining. The app logs this case rather than passing it on.
        var broken = plannerShaped
        broken["measurement"] = ["model": "vmaf_v0.6.1"]
        #expect(AdaptiveSearchStep(json: broken) == nil)
    }
}
