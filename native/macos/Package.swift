// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "PSRemoteSubMac",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "PSRemoteSubMac", type: .dynamic, targets: ["PSRemoteSubMac"]),
    ],
    targets: [
        .target(name: "PSRemoteSubMac"),
    ],
    swiftLanguageModes: [.v6]
)
