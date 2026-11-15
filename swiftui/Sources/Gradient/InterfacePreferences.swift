import SwiftUI

enum InterfacePreferences {
    static let themeKey = "interface.theme"
    static let languageKey = "interface.language"
    static let language = UserDefaults.standard.string(forKey: languageKey) ?? "uk"

    static func colorScheme(for theme: String) -> ColorScheme? {
        switch theme {
        case "light": return .light
        case "dark": return .dark
        default: return nil
        }
    }

    private struct Catalog: Decodable {
        let english: [String: String]
        let install: [String: String]
    }

    private static let catalog: Catalog? = {
        guard let url = Bundle.main.url(forResource: "InterfaceStrings", withExtension: "json")
                  ?? Bundle.module.url(forResource: "InterfaceStrings", withExtension: "json"),
              let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(Catalog.self, from: data)
    }()

    static func text(_ key: String) -> String {
        language == "en" ? catalog?.english[key] ?? key : key
    }

    static func installLabel(_ key: String) -> String {
        guard let label = catalog?.install[key] else { return key }
        return text(label)
    }
}

func tr(_ key: String, _ arguments: String...) -> String {
    let template = InterfacePreferences.text(key)
    guard !arguments.isEmpty else { return template }
    var result = ""
    var remainder = template[...]
    while let placeholder = remainder.range(of: #"\{[0-9]+\}"#, options: .regularExpression) {
        result += remainder[..<placeholder.lowerBound]
        let token = remainder[placeholder]
        if let index = Int(token.dropFirst().dropLast()), arguments.indices.contains(index) {
            result += arguments[index]
        } else {
            result += token
        }
        remainder = remainder[placeholder.upperBound...]
    }
    return result + String(remainder)
}
