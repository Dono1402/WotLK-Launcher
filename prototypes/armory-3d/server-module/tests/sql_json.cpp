#include "SqlJson.h"
#include <iostream>

int main()
{
    using namespace AtlasArmory;
    std::cout << "SELECT " << Object({
        {"name", Text("O'Brien\\test")}, {"empty", Text("")},
        {"level", Number(static_cast<unsigned char>(22))},
        {"haste", Number(-20.5)}, {"enabled", "CAST('true' AS JSON)"},
        {"schools", Array({Object({{"id", Number(1)}, {"crit", Number(2.5)}})})}
    }) << ";\n";
    auto const old = Object({{"capturedAtMs", Number(100)}, {"reason", Text("periodic")}});
    auto const recent = Object({{"capturedAtMs", Number(200)}, {"reason", Text("equipment")}});
    auto const logout = Object({{"capturedAtMs", Number(200)}, {"reason", Text("logout")}});
    for (auto const& [existing, incoming] : {std::pair{old, recent}, std::pair{recent, old},
        std::pair{recent, logout}, std::pair{Object({}), old}})
        std::cout << "SELECT " << NewestSnapshot(existing, incoming) << ";\n";
}
