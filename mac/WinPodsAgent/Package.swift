// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "WinPodsAgent",
    platforms: [.macOS(.v12)],
    targets: [
        .executableTarget(
            name: "WinPodsAgent",
            linkerSettings: [
                .linkedFramework("IOBluetooth"),
                .linkedFramework("AppKit"),
            ]
        ),
    ]
)
