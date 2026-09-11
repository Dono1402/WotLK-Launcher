#include "../src/atlas_shop_sql.h"
#include <iostream>
int main(int argc, char** argv)
{
    if (argc != 7) return 2;
    std::cout << AtlasShop::DeliverySql(argv[1], std::stoul(argv[2]), std::stoull(argv[3]), std::stoul(argv[4]), std::stoul(argv[5]), argv[6]);
}
