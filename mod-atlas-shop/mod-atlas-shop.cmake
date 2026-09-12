# The native account-service hooks in game include this module's public header.
# Keep the include scoped to game when building the complete Atlas source tree.
target_include_directories(game PRIVATE "${CMAKE_CURRENT_LIST_DIR}/src")
