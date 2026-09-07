#include "../src/atlas_chat_policy.h"

#include <cstdlib>
#include <iostream>

namespace
{
unsigned checks = 0;
#define CHECK(condition) do { ++checks; if (!(condition)) { \
    std::cerr << "FAIL at line " << __LINE__ << ": " << #condition << '\n'; std::exit(1); } } while (false)

void IdentityAndContent()
{
    CHECK(AtlasChat::ValidUsername("Dono_42"));
    CHECK(AtlasChat::WhisperName("Dono_42") == "Dono_42#Launcher");
    CHECK(AtlasChat::WhisperName(std::string(32, 'Z'))->size() == 41);
    CHECK(!AtlasChat::WhisperName("Dono_42#Launcher"));
    CHECK(!AtlasChat::WhisperName("Dono-Realm"));
    CHECK(!AtlasChat::WhisperName("|Hplayer:Admin|h"));
    CHECK(AtlasChat::ValidUsername(std::string(32, 'Z')));
    for (auto const& bad : { "", "ab", "Jean Paul", "Dono-Realm", "|Hplayer:Admin|h", "\" OR 1=1 --", "\xD0\x90" "dmin" })
        CHECK(!AtlasChat::ValidUsername(bad));
    CHECK(!AtlasChat::ValidUsername(std::string(33, 'x')));
    CHECK(AtlasChat::ValidBody("Bonjour, je suis dans le launcher."));
    CHECK(AtlasChat::ValidBody("日本語 😀 é |cffff0000 couleur |r"));
    CHECK(AtlasChat::ValidBody("Bonjour\nMonde\t!"));
    CHECK(!AtlasChat::ValidBody("Bonjour\rMonde"));
    CHECK(AtlasChat::ValidBody(std::string(1000, 'a')));
    CHECK(!AtlasChat::ValidBody(std::string(1001, 'a')));
    CHECK(!AtlasChat::ValidBody(" \n\r\t"));
    CHECK(!AtlasChat::ValidBody(std::string("A\0B", 3)));
    CHECK(!AtlasChat::ValidBody("a\x7f"));
    CHECK(!AtlasChat::ValidBody("a\xC2\x85"));
    CHECK(!AtlasChat::ValidBody("a\xE2\x80\xAE"));
    CHECK(!AtlasChat::ValidBody("a\xE2\x81\xA6"));
    for (auto const& bad : { "\x80", "\xC0\xAF", "\xC1\x81", "\xE0\x80\xAF", "\xED\xA0\x80",
        "\xF0\x80\x80\xAF", "\xF4\x90\x80\x80", "\xF5\x80\x80\x80", "\xFF", "\xE2\x82", "\xC2" })
        CHECK(!AtlasChat::ValidBody(bad));
    std::string emoji;
    for (int i = 0; i < 500; ++i)
        emoji += "😀";
    CHECK(emoji.size() == 2000);
    CHECK(AtlasChat::ValidBody(emoji));
    CHECK(!AtlasChat::ValidBody(emoji + "😀"));
    CHECK(AtlasChat::Hex("'\\\n") == "275c0a");
    CHECK(AtlasChat::Hex(std::string("\0\xFF", 2)) == "00ff");

    CHECK(AtlasChat::NormalizeBody("\r\n Bonjour\r\nMonde\t\n") == "Bonjour\nMonde");
    CHECK(AtlasChat::NormalizeBody("　\xC2\xA0" "Bonjour" "\xE2\x80\x89") == "Bonjour");
    CHECK(AtlasChat::NormalizeBody("\v\f Texte\f\v") == "Texte");
    CHECK(!AtlasChat::NormalizeBody("\r\n\t　\xC2\xA0"));
    CHECK(!AtlasChat::NormalizeBody("Bonjour\rMonde"));
    CHECK(!AtlasChat::NormalizeBody("invalid\xC0\xAF"));
    CHECK(!AtlasChat::NormalizeBody(std::string(8193, ' ')));
    CHECK(AtlasChat::NormalizeBody(" " + std::string(1000, 'x') + " ") == std::string(1000, 'x'));
}

void PlainRendering()
{
    CHECK(AtlasChat::RenderLines("", "Bonjour | et 日本語").front() == "Bonjour || et 日本語");
    std::string prefix = "[Atlas] Dono_42 : ";
    auto lines = AtlasChat::RenderLines(prefix, "Bonjour |Hplayer:Admin|h[Ami]|h |Ticon:80|t\nseconde ligne");
    CHECK(lines.size() == 1);
    CHECK(lines.front() == prefix + "Bonjour ||Hplayer:Admin||h[Ami]||h ||Ticon:80||t seconde ligne");
    CHECK(AtlasChat::RenderLines("bad|prefix", "message").empty());
    CHECK(AtlasChat::RenderLines(prefix, "\xED\xA0\x80").empty());
    CHECK(AtlasChat::RenderLines(prefix, "texte", prefix.size() + 3).empty());
    for (std::size_t cap = prefix.size() + 4; cap <= 255; ++cap)
    {
        std::string body, expected;
        for (unsigned i = 0; i < 95; ++i)
        {
            body += "é😀日|x\n";
            expected += "é😀日||x ";
        }
        auto fragments = AtlasChat::RenderLines(prefix, body, cap);
        CHECK(!fragments.empty());
        std::string joined;
        for (auto const& fragment : fragments)
        {
            CHECK(fragment.size() <= cap);
            CHECK(fragment.substr(0, prefix.size()) == prefix);
            auto content = fragment.substr(prefix.size());
            CHECK(!content.empty());
            std::size_t utf8Offset = 0;
            while (utf8Offset < content.size())
            {
                std::uint32_t cp = 0;
                CHECK(AtlasChat::ReadUtf8(content, utf8Offset, cp));
            }
            std::size_t at = 0;
            while ((at = content.find('|', at)) != std::string::npos)
            {
                CHECK(at + 1 < content.size() && content[at + 1] == '|');
                at += 2;
            }
            joined += content;
        }
        CHECK(joined == expected);
    }
}

void SessionAndDelivery()
{
    using AtlasChat::SessionStamp;
    SessionStamp first{ 42, 1001, 1, 1000000 };
    SessionStamp relogSameCharacter{ 42, 1001, 2, 1500000 };
    SessionStamp relogOtherCharacter{ 42, 1002, 3, 1500000 };
    SessionStamp otherAccount{ 84, 1001, 1, 1000000 };
    CHECK(first.Matches(first));
    CHECK(!first.Matches(relogSameCharacter));
    CHECK(!first.Matches(relogOtherCharacter));
    CHECK(!first.Matches(otherAccount));
    CHECK(!SessionStamp{}.Matches(SessionStamp{}));
    auto allowed = [&](SessionStamp const& live, SessionStamp const& claimed, unsigned sender = 84,
        unsigned recipient = 42, bool friends = true, std::int64_t created = 1100000,
        std::int64_t expires = 2000000, std::int64_t now = 1200000)
    {
        return AtlasChat::MayDeliver(live, claimed, sender, recipient, friends, created, expires, now);
    };
    CHECK(allowed(first, first));
    CHECK(!allowed(relogSameCharacter, first));
    CHECK(!allowed(relogOtherCharacter, first));
    CHECK(!allowed(otherAccount, first));
    CHECK(!allowed(first, first, 42));
    CHECK(!allowed(first, first, 84, 43));
    CHECK(!allowed(first, first, 84, 42, false));
    CHECK(!allowed(first, first, 84, 42, true, 999999));
    CHECK(allowed(first, first, 84, 42, true, 1000000));
    CHECK(!allowed(first, first, 84, 42, true, 1100000, 1200000, 1200000));
    CHECK(!allowed(relogSameCharacter, relogSameCharacter, 84, 42, true, 1100000, 2000000, 1600000));
    CHECK(allowed(relogSameCharacter, relogSameCharacter, 84, 42, true, 1500000, 2000000, 1600000));
}

void RateAndCapacity()
{
    AtlasChat::SubmissionLimiter limiter;
    for (unsigned i = 0; i < 30; ++i)
        CHECK(limiter.TryTake(42, 1000 + i));
    CHECK(!limiter.TryTake(42, 2000));
    CHECK(limiter.TryTake(84, 2000));
    limiter.Prune(60000);
    CHECK(!limiter.TryTake(42, 60999));
    CHECK(limiter.TryTake(42, 61000));
    for (unsigned account = 100; account < 4194; ++account)
        CHECK(limiter.TryTake(account, 62000));
    CHECK(!limiter.TryTake(5000, 62000));
    limiter.Prune(182000);
    CHECK(limiter.TryTake(5000, 182000));
}
}

int main()
{
    IdentityAndContent();
    PlainRendering();
    SessionAndDelivery();
    RateAndCapacity();
    std::cout << "PASS: " << checks << " assertions; UTF-8, plain-text fragmentation, session/account isolation, expiry, friendship and rate limits.\n";
}
