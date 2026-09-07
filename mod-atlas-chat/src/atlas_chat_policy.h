#ifndef ATLAS_CHAT_POLICY_H
#define ATLAS_CHAT_POLICY_H

#include <algorithm>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace AtlasChat
{
// Independent of AzerothCore so the byte/identity boundaries can be unit tested.
inline bool ReadUtf8(std::string_view text, std::size_t& offset, std::uint32_t& codepoint)
{
    if (offset >= text.size())
        return false;
    auto lead = static_cast<unsigned char>(text[offset++]);
    if (lead < 0x80)
    {
        codepoint = lead;
        return true;
    }
    unsigned length = lead >= 0xC2 && lead <= 0xDF ? 2 :
        lead >= 0xE0 && lead <= 0xEF ? 3 : lead >= 0xF0 && lead <= 0xF4 ? 4 : 0;
    if (!length || offset + length - 1 > text.size())
        return false;
    codepoint = lead & ((1u << (7u - length)) - 1u);
    for (unsigned i = 1; i < length; ++i)
    {
        auto next = static_cast<unsigned char>(text[offset++]);
        if ((next & 0xC0) != 0x80)
            return false;
        codepoint = (codepoint << 6) | (next & 0x3F);
    }
    return (length != 2 || codepoint >= 0x80) &&
        (length != 3 || codepoint >= 0x800) &&
        (length != 4 || codepoint >= 0x10000) &&
        codepoint <= 0x10FFFF && !(codepoint >= 0xD800 && codepoint <= 0xDFFF);
}

inline bool ValidUsername(std::string_view name)
{
    return name.size() >= 3 && name.size() <= 32 &&
        std::all_of(name.begin(), name.end(), [](unsigned char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') || c == '_';
        });
}

inline bool ValidBody(std::string_view text)
{
    if (text.empty() || text.size() > 4000)
        return false;
    std::size_t offset = 0, count = 0;
    bool hasText = false;
    while (offset < text.size())
    {
        std::uint32_t cp = 0;
        if (!ReadUtf8(text, offset, cp))
            return false;
        count += cp > 0xFFFF ? 2 : 1; // Matches the launcher's .NET UTF-16 limit.
        if (count > 1000 ||
            (cp < 32 && cp != '\n' && cp != '\t') ||
            (cp >= 0x7F && cp <= 0x9F) || (cp >= 0x202A && cp <= 0x202E) ||
            (cp >= 0x2066 && cp <= 0x2069))
            return false;
        hasText = hasText || (cp > 32 && cp != 0xA0 && cp != 0x3000);
    }
    return hasText;
}

inline bool IsTrimSpace(std::uint32_t cp)
{
    // Char.IsWhiteSpace / String.Trim on the API's UTF-16 text.
    return (cp >= 9 && cp <= 13) || cp == 0x20 || cp == 0x85 || cp == 0xA0 ||
        cp == 0x1680 || (cp >= 0x2000 && cp <= 0x200A) || cp == 0x2028 ||
        cp == 0x2029 || cp == 0x202F || cp == 0x205F || cp == 0x3000;
}

inline std::optional<std::string> NormalizeBody(std::string_view input)
{
    if (input.size() > 8192)
        return std::nullopt;
    std::string value;
    value.reserve(input.size());
    for (std::size_t i = 0; i < input.size(); ++i)
        if (input[i] != '\r' || i + 1 == input.size() || input[i + 1] != '\n')
            value.push_back(input[i]);
    std::size_t offset = 0, first = value.size(), last = 0;
    while (offset < value.size())
    {
        auto start = offset;
        std::uint32_t cp = 0;
        if (!ReadUtf8(value, offset, cp))
            return std::nullopt;
        if (!IsTrimSpace(cp))
        {
            first = std::min(first, start);
            last = offset;
        }
    }
    if (first >= last)
        return std::nullopt;
    std::string body = value.substr(first, last - first);
    return ValidBody(body) ? std::optional<std::string>(std::move(body)) : std::nullopt;
}

inline std::string Hex(std::string_view value)
{
    constexpr char digits[] = "0123456789abcdef";
    std::string result;
    result.reserve(value.size() * 2);
    for (unsigned char c : value)
    {
        result.push_back(digits[c >> 4]);
        result.push_back(digits[c & 15]);
    }
    return result;
}

// Plain content stays plain: a pipe is an indivisible escaped pair, UTF-8 code
// points are never split, and user newlines cannot forge a second chat line.
inline std::vector<std::string> RenderLines(std::string_view prefix, std::string_view body,
    std::size_t maxBytes = 230)
{
    if (!ValidBody(body) || prefix.size() + 4 > maxBytes || prefix.find('|') != std::string_view::npos)
        return {};
    std::vector<std::string> lines;
    std::string line(prefix);
    std::size_t offset = 0;
    while (offset < body.size())
    {
        auto start = offset;
        std::uint32_t cp = 0;
        if (!ReadUtf8(body, offset, cp))
            return {};
        std::string unit = cp == '|' ? "||" : cp < 32 ? " " : std::string(body.substr(start, offset - start));
        if (line.size() + unit.size() > maxBytes)
        {
            lines.push_back(std::move(line));
            line = std::string(prefix);
        }
        line += unit;
    }
    if (line.size() > prefix.size())
        lines.push_back(std::move(line));
    return lines;
}

struct SessionStamp
{
    std::uint32_t Account = 0;
    std::uint32_t Character = 0;
    std::uint64_t Generation = 0;
    std::int64_t JoinedUtcMicros = 0;

    bool Matches(SessionStamp const& other) const
    {
        return Account && Character && Generation && Account == other.Account &&
            Character == other.Character && Generation == other.Generation;
    }
};

inline bool MayDeliver(SessionStamp const& current, SessionStamp const& claimed,
    std::uint32_t senderAccount, std::uint32_t recipientAccount, bool stillFriends,
    std::int64_t createdUtcMicros, std::int64_t expiresUtcMicros, std::int64_t nowUtcMicros)
{
    return current.Matches(claimed) && current.Account == recipientAccount &&
        senderAccount != recipientAccount && stillFriends &&
        createdUtcMicros >= current.JoinedUtcMicros && nowUtcMicros < expiresUtcMicros;
}

class SubmissionLimiter
{
public:
    bool TryTake(std::uint32_t account, std::uint64_t nowMs)
    {
        auto existing = _windows.find(account);
        if (existing == _windows.end())
        {
            if (_windows.size() >= 4096)
                return false;
            existing = _windows.emplace(account, Window{ nowMs, 0 }).first;
        }
        auto& window = existing->second;
        if (nowMs - window.Start >= 60000)
            window = { nowMs, 0 };
        if (window.Count >= 30)
            return false;
        ++window.Count;
        return true;
    }

    void Prune(std::uint64_t nowMs)
    {
        for (auto it = _windows.begin(); it != _windows.end();)
            if (nowMs - it->second.Start >= 120000)
                it = _windows.erase(it);
            else
                ++it;
    }

private:
    struct Window { std::uint64_t Start; unsigned Count; };
    std::unordered_map<std::uint32_t, Window> _windows;
};
}
#endif
