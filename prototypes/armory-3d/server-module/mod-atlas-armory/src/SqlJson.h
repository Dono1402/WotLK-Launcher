#pragma once

#include <cmath>
#include <initializer_list>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>
#include <fmt/format.h>

namespace AtlasArmory
{
// MySQL constructs typed JSON. Text uses hex literals, independent of SQL escaping modes.
inline std::string Text(std::string const& value)
{
    if (value.empty())
        return "''";
    std::string hex;
    for (unsigned char byte : value)
        hex += fmt::format("{:02x}", byte);
    return "CONVERT(0x" + hex + " USING utf8mb4)";
}

template <typename T> std::string Number(T value)
{
    if (!std::isfinite(static_cast<double>(value)))
        throw std::runtime_error("Non-finite engine statistic");
    return fmt::format("{}", value);
}

inline std::string Array(std::vector<std::string> const& elements)
{
    std::string sql = "JSON_ARRAY(";
    for (std::size_t i = 0; i < elements.size(); ++i)
        sql += (i ? "," : "") + elements[i];
    return sql + ")";
}

inline std::string Object(std::initializer_list<std::pair<std::string, std::string>> fields)
{
    std::string sql = "JSON_OBJECT(";
    bool first = true;
    for (auto const& [key, value] : fields)
    {
        if (!first)
            sql += ",";
        sql += Text(key) + "," + value;
        first = false;
    }
    return sql + ")";
}

// Async DB workers may finish out of order. Arguments are trusted SQL expressions, not data.
inline std::string NewestSnapshot(std::string const& existing, std::string const& incoming)
{
    auto const time = [](std::string const& value)
    {
        return "COALESCE(CAST(JSON_UNQUOTE(JSON_EXTRACT(" + value +
            ",'$.capturedAtMs')) AS UNSIGNED),0)";
    };
    return "IF(" + time(incoming) + ">=" + time(existing) + "," + incoming + "," + existing + ")";
}
}
