// Methods keep their identities, and every name part survives a container round trip.

#include "sherlock/storage/profile.hpp"

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

constexpr Mvid kLibA{0xa1};
constexpr Mvid kLibB{0xb2};

MethodDescription method(const Mvid& mvid, std::uint32_t token, std::string_view type = "Mapper",
                         std::string_view name = "Map", std::string_view parameters = "(int)") {
    MethodDescription m;
    m.mvid = mvid;
    m.token = token;
    m.module = mvid == kLibA ? "LibA.dll" : "LibB.dll";
    m.ns = "App";
    m.type = type;
    m.name = name;
    m.parameters = parameters;
    m.qualifiedParameters = parameters;
    m.returnType = "void";
    m.qualifiedReturnType = "void";
    return m;
}

SymbolTable roundTrip(const SymbolWriter& symbols, std::string& bytes) {
    ContainerWriter w(SlabKind::Profile, kProfileVersion);
    symbols.writeTo(w);
    bytes = w.finish();
    ContainerReader c(asBytes(bytes));
    EXPECT_TRUE(c.valid());
    return SymbolTable::read(c);
}

} // namespace

TEST(Symbols, MethodsAreKeptApartByIdentityNotByName) {
    SymbolWriter s;
    const std::uint32_t libA = s.internMethod(method(kLibA, 0x06000001));
    const std::uint32_t libB = s.internMethod(method(kLibB, 0x06000001)); // same names, another assembly
    const std::uint32_t overload = s.internMethod(method(kLibA, 0x06000002));
    EXPECT_NE(libA, libB);
    EXPECT_NE(libA, overload);
    // The same assembly loaded twice (two load contexts) is the same method.
    EXPECT_EQ(s.internMethod(method(kLibA, 0x06000001, "Renamed", "Ignored")), libA);
    EXPECT_EQ(s.methodCount(), 3u);
}

TEST(Symbols, MethodsWithoutAKnownModuleNeverMerge) {
    SymbolWriter s;
    MethodDescription unknown = method(Mvid{}, 0x06000001);
    unknown.problem = "GetModuleMetaData failed";
    EXPECT_NE(s.internMethod(unknown), s.internMethod(unknown));
    EXPECT_EQ(s.methodCount(), 2u);
}

TEST(Symbols, AMethodNamedThroughAnyLoadOfItsModuleKeepsTheName) {
    SymbolWriter s;
    MethodDescription unresolved;
    unresolved.mvid = kLibA;
    unresolved.token = 0x06000001;
    unresolved.module = "LibA.dll";
    unresolved.problem = "signature failed";
    const std::uint32_t id = s.internMethod(unresolved);
    EXPECT_EQ(s.internMethod(method(kLibA, 0x06000001)), id);
    EXPECT_EQ(s.internMethod(unresolved), id); // a later problem doesn't replace the name

    std::string bytes;
    SymbolTable table = roundTrip(s, bytes);
    ASSERT_EQ(table.methodCount(), 1u);
    EXPECT_EQ(table.text(table.method(id).name), "Map");
    EXPECT_EQ(table.text(table.method(id).problem), "");
}

TEST(Symbols, RoundTripsEveryNamePartTypesAndStacks) {
    SymbolWriter s;
    MethodDescription detailed = method(kLibA, 0x0600000a, "Outer<T>+Inner", "System.IDisposable.Dispose", "(ref int)");
    detailed.genericParameters = "<U>";
    detailed.qualifiedParameters = "(ref System.Int32)";
    detailed.returnType = "List<T>";
    detailed.qualifiedReturnType = "System.Collections.Generic.List<T>";
    const std::uint32_t main = s.internMethod(method(kLibB, 0x06000001, "Program", "Main", "(string[])"));
    const std::uint32_t dispose = s.internMethod(detailed);
    const std::uint32_t type = s.internType("System.Collections.Generic.List<App.Order>");
    const std::vector<std::uint32_t> frames = {main, dispose};
    const std::uint32_t stack = s.internStack(frames);
    EXPECT_EQ(s.internStack(frames), stack);
    EXPECT_NE(s.internStack(std::vector<std::uint32_t>{dispose, main}), stack); // order matters

    std::string bytes;
    SymbolTable t = roundTrip(s, bytes);
    ASSERT_EQ(t.methodCount(), 2u);
    ASSERT_EQ(t.moduleCount(), 2u);
    const MethodRecord& m = t.method(dispose);
    EXPECT_EQ(t.module(m.module).mvid, kLibA);
    EXPECT_EQ(t.text(t.module(m.module).name), "LibA.dll");
    EXPECT_EQ(m.token, 0x0600000au);
    EXPECT_EQ(t.text(m.ns), "App");
    EXPECT_EQ(t.text(m.type), "Outer<T>+Inner");
    EXPECT_EQ(t.text(m.name), "System.IDisposable.Dispose");
    EXPECT_EQ(t.text(m.genericParameters), "<U>");
    EXPECT_EQ(t.text(m.parameters), "(ref int)");
    EXPECT_EQ(t.text(m.qualifiedParameters), "(ref System.Int32)");
    EXPECT_EQ(t.text(m.returnType), "List<T>");
    EXPECT_EQ(t.text(m.qualifiedReturnType), "System.Collections.Generic.List<T>");
    EXPECT_EQ(t.text(m.problem), "");
    EXPECT_EQ(t.type(type), "System.Collections.Generic.List<App.Order>");
    std::span<const std::uint32_t> got = t.stackFrames(stack);
    ASSERT_EQ(got.size(), 2u);
    EXPECT_EQ(got[0], main);
    EXPECT_EQ(got[1], dispose);
}

TEST(Symbols, SharedNamePartsAreStoredOnce) {
    SymbolWriter s;
    for (std::uint32_t token = 1; token <= 100; ++token) {
        s.internMethod(method(kLibA, 0x06000000 + token));
    }
    std::string bytes;
    SymbolTable t = roundTrip(s, bytes);
    EXPECT_EQ(t.method(0).ns.offset, t.method(99).ns.offset);
    EXPECT_EQ(t.methodCount(), 100u);
    EXPECT_EQ(t.moduleCount(), 1u);
}

TEST(Symbols, EmptyWriterWritesEmptyTables) {
    std::string bytes;
    SymbolTable t = roundTrip(SymbolWriter{}, bytes);
    EXPECT_EQ(t.methodCount(), 0u);
    EXPECT_EQ(t.moduleCount(), 0u);
    EXPECT_EQ(t.typeCount(), 0u);
    EXPECT_EQ(t.stackCount(), 0u);
}
