// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "Gradient",
    platforms: [.macOS(.v15)],
    targets: [
        .binaryTarget(
            name: "Sparkle",
            url: "https://github.com/sparkle-project/Sparkle/releases/download/2.10.1/Sparkle-for-Swift-Package-Manager.zip",
            checksum: "4c2e48a3e50ebcc2c88a58a4c7264a1a99c3c31857ce754357afb9cdb323022a"
        ),
        .executableTarget(
            name: "Gradient",
            dependencies: ["Sparkle"],
            resources: [.copy("Resources/Cover.png"), .copy("Resources/InterfaceStrings.json")],
            linkerSettings: [.unsafeFlags(["-Xlinker", "-rpath", "-Xlinker", "@executable_path/../Frameworks"])]
        )
    ],
    swiftLanguageModes: [.v5]
)
