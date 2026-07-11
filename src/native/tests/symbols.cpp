// Tests for the interned symbol tables (Layer 2): frames dedup by name, stacks dedup by
// frame-id sequence, and both round-trip through the container back to the same names/frames.

#include "sherlock/storage/symbols.hpp"

#include <gtest/gtest.h>

#include <cstddef>
#include <cstdint>
#include <span>
#include <string>
#include <vector>

using namespace Sherlock::storage;

namespace {
std::span<const std::byte> asBytes(const std::string& s) {
    return {reinterpret_cast<const std::byte*>(s.data()), s.size()};
}
std::span<const std::uint32_t> ids(const std::vector<std::uint32_t>& v) { return {v.data(), v.size()}; }
} // namespace

TEST(Symbols, DedupsFramesAndStacks) {
    StackInterner s;
    const std::uint32_t main = s.internFrame("Program.Main");
    const std::uint32_t add = s.internFrame("Registry.Add");
    const std::uint32_t resize = s.internFrame("List.Resize");
    EXPECT_EQ(s.internFrame("Program.Main"), main); // same name → same id
    EXPECT_EQ(s.frameCount(), 3u);

    const std::vector<std::uint32_t> a = {main, add};
    const std::vector<std::uint32_t> b = {main, add, resize};
    const std::uint32_t sa = s.internStack(ids(a));
    const std::uint32_t sb = s.internStack(ids(b));
    EXPECT_EQ(s.internStack(ids(a)), sa); // identical stack → same id
    EXPECT_NE(sa, sb);
    EXPECT_EQ(s.stackCount(), 2u);
}

TEST(Symbols, RoundTripsThroughContainer) {
    StackInterner s;
    const std::uint32_t main = s.internFrame("Program.Main");
    const std::uint32_t add = s.internFrame("Registry.Add");
    const std::uint32_t resize = s.internFrame("List.Resize");
    const std::vector<std::uint32_t> deep = {main, add, resize};
    const std::uint32_t sid = s.internStack(ids(deep));

    ContainerWriter w;
    s.writeTo(w);
    const std::string bytes = w.finish();

    ContainerReader c(asBytes(bytes));
    ASSERT_TRUE(c.valid());
    StackTable t = StackTable::read(c);

    ASSERT_EQ(t.frameCount(), 3u);
    EXPECT_EQ(t.frame(main), "Program.Main");
    EXPECT_EQ(t.frame(add), "Registry.Add");
    EXPECT_EQ(t.frame(resize), "List.Resize");

    std::span<const std::uint32_t> got = t.stackFrames(sid);
    ASSERT_EQ(got.size(), 3u);
    EXPECT_EQ(got[0], main);
    EXPECT_EQ(got[1], add);
    EXPECT_EQ(got[2], resize);
    // Resolve the whole stack to names.
    EXPECT_EQ(t.frame(got[2]), "List.Resize");
}

TEST(Symbols, EmptyInternerWritesEmptyTables) {
    StackInterner s;
    ContainerWriter w;
    s.writeTo(w);
    ContainerReader c(asBytes(w.finish()));
    ASSERT_TRUE(c.valid());
    StackTable t = StackTable::read(c);
    EXPECT_EQ(t.frameCount(), 0u);
    EXPECT_EQ(t.stackCount(), 0u);
}
