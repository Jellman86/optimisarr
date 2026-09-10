import ServiceManagement

/// Whether this app opens at login, through the system's own login-item service so it appears in
/// System Settings › General › Login Items like any other app and can be switched off there too.
/// Requires the app to run from a real bundle, which `make-app.sh` produces; from a bare build
/// directory the service refuses and the toggle says so.
enum LoginItem {
    static var isEnabled: Bool {
        SMAppService.mainApp.status == .enabled
    }

    static func setEnabled(_ enabled: Bool) throws {
        if enabled {
            try SMAppService.mainApp.register()
        } else {
            try SMAppService.mainApp.unregister()
        }
    }
}
