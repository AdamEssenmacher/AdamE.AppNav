// swift-tools-version:6.0
import Foundation
import PackageDescription

let environment = ProcessInfo.processInfo.environment
let packages = environment["NUGET_PACKAGES"].flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0) } ??
    FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".nuget/packages")
let runtime = environment["OAHU_RUNTIME_SWIFT"].flatMap { $0.isEmpty ? nil : $0 } ??
    packages.appendingPathComponent("oahu.ios/0.1.0-preview.g7a56b3ba/swift/OahuRuntime").path

let package = Package(
    name: "ProbeScreens",
    platforms: [.iOS(.v18)],
    products: [.library(name: "ProbeScreens", type: .dynamic, targets: ["ProbeScreens"])],
    dependencies: [
        .package(name: "OahuRuntime", path: runtime),
        .package(name: "AppNavSwiftUI", path: "../../../src/AdamE.AppNav.SwiftUI/Swift")
    ],
    targets: [.target(name: "ProbeScreens", dependencies: [
        .product(name: "OahuRuntime", package: "OahuRuntime"),
        .product(name: "AppNavSwiftUI", package: "AppNavSwiftUI")
    ])]
)
