import Foundation
import SwiftUI

struct ProjectDescription: View {
    private struct Badge {
        let label: String
        let image: URL
        let destination: URL
    }

    private enum Block {
        case text(String)
        case badges([Badge])
    }

    private static let linkedImage = try! NSRegularExpression(
        pattern: #"\[!\[([^\]\r\n]*)\]\(\s*<?(https://[^\s<>()]+)>?\s*\)\]\(\s*<?(https://[^\s<>()]+)>?\s*\)"#
    )

    private let blocks: [Block]

    init(_ markdown: String) {
        let paragraphs = markdown
            .replacingOccurrences(of: "\r\n", with: "\n")
            .components(separatedBy: "\n\n")
        var parsed: [Block] = []
        var text: [String] = []
        for paragraph in paragraphs {
            if let badges = Self.badges(in: paragraph) {
                if !text.isEmpty {
                    parsed.append(.text(text.joined(separator: "\n\n")))
                    text.removeAll()
                }
                parsed.append(.badges(badges))
            } else {
                text.append(paragraph)
            }
        }
        if !text.isEmpty { parsed.append(.text(text.joined(separator: "\n\n"))) }
        blocks = parsed
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            ForEach(blocks.indices, id: \.self) { index in
                switch blocks[index] {
                case let .text(text):
                    Text(text).textSelection(.enabled)
                case let .badges(badges):
                    BadgeRow {
                        ForEach(badges.indices, id: \.self) { position in
                            badgeLink(badges[position])
                        }
                    }
                }
            }
        }
    }

    private func badgeLink(_ badge: Badge) -> some View {
        Link(destination: badge.destination) {
            AsyncImage(url: badge.image) { phase in
                if let image = phase.image {
                    image.resizable()
                        .scaledToFit()
                        .frame(maxWidth: 220, maxHeight: 34)
                        .fixedSize()
                } else {
                    Text(badge.label).font(.callout).lineLimit(1)
                        .padding(.horizontal, 8)
                        .frame(height: 34)
                }
            }
        }
        .buttonStyle(.plain)
        .accessibilityLabel(Text(badge.label))
        .help(badge.label)
    }

    private static func badges(in paragraph: String) -> [Badge]? {
        let range = NSRange(paragraph.startIndex..., in: paragraph)
        let matches = linkedImage.matches(in: paragraph, range: range)
        guard matches.count > 1,
              linkedImage.stringByReplacingMatches(in: paragraph, range: range, withTemplate: "")
                .trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }

        let badges = matches.compactMap { match -> Badge? in
            guard let label = Range(match.range(at: 1), in: paragraph),
                  let imageRange = Range(match.range(at: 2), in: paragraph),
                  let destinationRange = Range(match.range(at: 3), in: paragraph),
                  let image = URL(string: String(paragraph[imageRange])), image.host != nil,
                  let destination = URL(string: String(paragraph[destinationRange])), destination.host != nil
            else { return nil }
            return Badge(label: String(paragraph[label]), image: image, destination: destination)
        }
        return badges.count == matches.count ? badges : nil
    }

    private struct BadgeRow: Layout {
        func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
            arrange(width: proposal.width ?? .infinity, subviews: subviews).size
        }

        func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
            let layout = arrange(width: bounds.width, subviews: subviews)
            for (subview, frame) in zip(subviews, layout.frames) {
                subview.place(
                    at: CGPoint(x: bounds.minX + frame.minX, y: bounds.minY + frame.minY),
                    anchor: .topLeading,
                    proposal: ProposedViewSize(width: frame.width, height: frame.height)
                )
            }
        }

        private func arrange(width: CGFloat, subviews: Subviews) -> (size: CGSize, frames: [CGRect]) {
            let spacing: CGFloat = 6
            var x: CGFloat = 0
            var y: CGFloat = 0
            var rowHeight: CGFloat = 0
            var totalWidth: CGFloat = 0
            var frames: [CGRect] = []
            for subview in subviews {
                let size = subview.sizeThatFits(.unspecified)
                if x > 0 && x + size.width > width {
                    x = 0
                    y += rowHeight + spacing
                    rowHeight = 0
                }
                frames.append(CGRect(origin: CGPoint(x: x, y: y), size: size))
                totalWidth = max(totalWidth, x + size.width)
                rowHeight = max(rowHeight, size.height)
                x += size.width + spacing
            }
            return (CGSize(width: totalWidth, height: y + rowHeight), frames)
        }
    }
}
