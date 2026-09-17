import Foundation

public extension Pipe {
    /// Keeps this pipe out of every other process this app starts.
    ///
    /// A pipe Foundation creates is not close-on-exec, so it is inherited by anything spawned
    /// while it is open — a load probe, a preview frame, another job's FFmpeg. The process the
    /// pipe was made for then exits and the read end still does not reach end-of-file, because a
    /// descriptor somewhere else is holding it open, and whoever was reading it waits for as long
    /// as that unrelated process happens to live.
    ///
    /// That is not a theoretical race. It left a Mac sat in "Measuring" for thirty-six minutes
    /// with no FFmpeg running at all and its lease renewing perfectly underneath it, and it is
    /// reproducible in a second: run two processes at once, let one of them outlive the other,
    /// and watch the short one's reader wait for the long one.
    ///
    /// `dup2` clears the flag on the descriptor it installs, so the child this pipe is *for*
    /// still receives it as its own stdout or stderr. Only the copies that nobody asked for go.
    func sealFromOtherChildren() {
        for handle in [fileHandleForReading, fileHandleForWriting] {
            _ = fcntl(handle.fileDescriptor, F_SETFD, FD_CLOEXEC)
        }
    }
}
