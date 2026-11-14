// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "Gradient",
    platforms: [.macOS(.v15)],
    targets: [
        .executableTarget(name: "Gradient", resources: [.copy("Resources/Cover.png")])
    ],
    swiftLanguageModes: [.v5]
)
