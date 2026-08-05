// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "NetWatcherLimiter",
    platforms: [
        .macOS(.v15)
    ],
    products: [
        .library(
            name: "NetWatcherLimiterCore",
            targets: ["NetWatcherLimiterCore"]),
        .library(
            name: "NetWatcherFilterExtension",
            targets: ["NetWatcherFilterExtension"]),
        .library(
            name: "NetWatcherLimiterHostSupport",
            targets: ["NetWatcherLimiterHostSupport"]),
        .library(
            name: "NetWatcherLimiterXPC",
            targets: ["NetWatcherLimiterXPC"]),
        .executable(
            name: "netwatcher-limiter-diagnostics",
            targets: ["NetWatcherLimiterDiagnostics"]),
        .executable(
            name: "netwatcher-limiter-host",
            targets: ["NetWatcherLimiterHost"])
    ],
    targets: [
        .target(
            name: "NetWatcherLimiterCore"),
        .target(
            name: "NetWatcherFilterExtension",
            dependencies: ["NetWatcherLimiterCore"]),
        .target(
            name: "NetWatcherLimiterHostSupport",
            dependencies: ["NetWatcherLimiterCore"],
            linkerSettings: [
                .linkedFramework("Security"),
                .linkedFramework("AppKit")
            ]),
        .target(
            name: "NetWatcherLimiterXPC",
            dependencies: [
                "NetWatcherLimiterCore",
                "NetWatcherLimiterHostSupport"
            ]),
        .executableTarget(
            name: "NetWatcherLimiterDiagnostics",
            dependencies: ["NetWatcherLimiterCore"]),
        .executableTarget(
            name: "NetWatcherLimiterHost",
            dependencies: [
                "NetWatcherLimiterCore",
                "NetWatcherLimiterHostSupport"
            ])
    ]
)
