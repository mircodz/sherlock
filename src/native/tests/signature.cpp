#include "sherlock/profiler/signature.hpp"

#include <gtest/gtest.h>

#include <map>
#include <optional>
#include <string>
#include <vector>

using namespace Sherlock;

namespace {

constexpr mdToken kList = 0x01000005;    // TypeRef row 5
constexpr BYTE kListCoded = (5 << 2) | 1;
constexpr mdToken kOwner = 0x02000003;   // TypeDef row 3
constexpr BYTE kOwnerCoded = (3 << 2) | 0;
constexpr mdTypeSpec kSpec = 0x1b000001; // TypeSpec row 1
constexpr BYTE kSpecCoded = (1 << 2) | 2;

struct Fixture {
    std::map<mdToken, std::string> names{{kList, "System.Collections.Generic.List`1"}, {kOwner, "Example.Owner"}};
    std::vector<BYTE> spec;
    std::vector<std::string> typeParameters{"TKey", "TValue"};
    std::vector<std::string> methodParameters{"T"};

    std::optional<std::string> format(const std::vector<BYTE>& blob, bool includeReturnType = false) const {
        signature::Context context{
            [this](mdToken token) { return names.at(token); },
            [this](mdTypeSpec token) {
                EXPECT_EQ(token, kSpec);
                return std::span<const BYTE>(spec);
            },
            typeParameters,
            methodParameters,
        };
        return signature::format(blob, context, includeReturnType);
    }
};

} // namespace

TEST(Signature, FormatsPrimitivesAsCSharpKeywords) {
    Fixture fixture;
    EXPECT_EQ(fixture.format({0x20, 0x00, ELEMENT_TYPE_VOID}), "()");
    EXPECT_EQ(fixture.format({0x00, 0x03, ELEMENT_TYPE_VOID, ELEMENT_TYPE_I4, ELEMENT_TYPE_STRING, ELEMENT_TYPE_U}),
              "(int, string, nuint)");
    EXPECT_EQ(fixture.format({0x00, 0x02, ELEMENT_TYPE_VOID, ELEMENT_TYPE_OBJECT, ELEMENT_TYPE_R8}), "(object, double)");
}

TEST(Signature, FormatsTypeTokensWithShortNames) {
    Fixture fixture;
    EXPECT_EQ(fixture.format({0x00, 0x01, ELEMENT_TYPE_VOID, ELEMENT_TYPE_CLASS, kOwnerCoded}), "(Owner)");
    EXPECT_EQ(fixture.format({0x00, 0x01, ELEMENT_TYPE_VOID, ELEMENT_TYPE_GENERICINST, ELEMENT_TYPE_CLASS, kListCoded, 0x01,
                              ELEMENT_TYPE_STRING}),
              "(List<string>)");
}

TEST(Signature, ResolvesTypeAndMethodGenericParameters) {
    Fixture fixture;
    // static void M<T>(T, List<TValue>)
    EXPECT_EQ(fixture.format({0x10, 0x01, 0x02, ELEMENT_TYPE_VOID, ELEMENT_TYPE_MVAR, 0x00, ELEMENT_TYPE_GENERICINST,
                              ELEMENT_TYPE_CLASS, kListCoded, 0x01, ELEMENT_TYPE_VAR, 0x01}),
              "(T, List<TValue>)");
    EXPECT_EQ(fixture.format({0x00, 0x01, ELEMENT_TYPE_VOID, ELEMENT_TYPE_VAR, 0x02}), std::nullopt);
    EXPECT_EQ(fixture.format({0x00, 0x01, ELEMENT_TYPE_VOID, ELEMENT_TYPE_MVAR, 0x01}), std::nullopt);
}

TEST(Signature, FormatsArraysPointersAndByRefs) {
    Fixture fixture;
    EXPECT_EQ(fixture.format({0x00, 0x04, ELEMENT_TYPE_VOID, ELEMENT_TYPE_SZARRAY, ELEMENT_TYPE_U1, ELEMENT_TYPE_ARRAY,
                              ELEMENT_TYPE_I4, 0x02, 0x00, 0x00, ELEMENT_TYPE_BYREF, ELEMENT_TYPE_I8, ELEMENT_TYPE_PTR,
                              ELEMENT_TYPE_VOID}),
              "(byte[], int[,], ref long, void*)");
    // Rank-1 MD array with sizes and lower bounds: int[*]
    EXPECT_EQ(fixture.format({0x00, 0x01, ELEMENT_TYPE_VOID, ELEMENT_TYPE_ARRAY, ELEMENT_TYPE_I4, 0x01, 0x01, 0x04, 0x01,
                              0x00}),
              "(int[*])");
}

TEST(Signature, SkipsCustomModifiersAndExpandsTypeSpecs) {
    Fixture fixture;
    fixture.spec = {ELEMENT_TYPE_SZARRAY, ELEMENT_TYPE_STRING};
    EXPECT_EQ(fixture.format({0x00, 0x02, ELEMENT_TYPE_VOID, ELEMENT_TYPE_CMOD_REQD, kOwnerCoded, ELEMENT_TYPE_I4,
                              ELEMENT_TYPE_CLASS, kSpecCoded}),
              "(int, string[])");
}

TEST(Signature, FormatsFunctionPointersAndVarargs) {
    Fixture fixture;
    EXPECT_EQ(fixture.format({0x00, 0x01, ELEMENT_TYPE_VOID, ELEMENT_TYPE_FNPTR, 0x00, 0x01, ELEMENT_TYPE_I4,
                              ELEMENT_TYPE_STRING}),
              "(delegate*<string, int>)");
    EXPECT_EQ(fixture.format({0x05, 0x01, ELEMENT_TYPE_VOID, ELEMENT_TYPE_I4}), "(int, __arglist)");
    EXPECT_EQ(fixture.format({0x05, 0x00, ELEMENT_TYPE_VOID}), "(__arglist)");
}

TEST(Signature, AppendsReturnTypeOnRequest) {
    Fixture fixture;
    EXPECT_EQ(fixture.format({0x00, 0x01, ELEMENT_TYPE_I4, ELEMENT_TYPE_STRING}, true), "(string):int");
}

TEST(Signature, RejectsMalformedAndNonMethodSignatures) {
    Fixture fixture;
    EXPECT_EQ(fixture.format({}), std::nullopt);
    EXPECT_EQ(fixture.format({0x00, 0x02, ELEMENT_TYPE_VOID, ELEMENT_TYPE_I4}), std::nullopt);
    EXPECT_EQ(fixture.format({IMAGE_CEE_CS_CALLCONV_FIELD, ELEMENT_TYPE_I4}), std::nullopt);
    EXPECT_EQ(fixture.format({IMAGE_CEE_CS_CALLCONV_LOCAL_SIG, 0x00}), std::nullopt);
    EXPECT_EQ(fixture.format({0x00, 0x01, ELEMENT_TYPE_VOID, ELEMENT_TYPE_PINNED, ELEMENT_TYPE_I4}), std::nullopt);
    EXPECT_EQ(fixture.format({0x00, 0x01, ELEMENT_TYPE_VOID, ELEMENT_TYPE_CLASS, 0x03}), std::nullopt); // bad table
    EXPECT_EQ(fixture.format({0x00, 0x01, ELEMENT_TYPE_VOID, ELEMENT_TYPE_GENERICINST, ELEMENT_TYPE_CLASS, kListCoded,
                              0x00}),
              std::nullopt);
}

TEST(Signature, BoundsRecursionAndOutputSize) {
    Fixture fixture;
    std::vector<BYTE> nested{0x00, 0x01, ELEMENT_TYPE_VOID};
    nested.insert(nested.end(), 200, ELEMENT_TYPE_SZARRAY);
    nested.push_back(ELEMENT_TYPE_I4);
    EXPECT_EQ(fixture.format(nested), std::nullopt);

    fixture.spec = {ELEMENT_TYPE_SZARRAY, ELEMENT_TYPE_CLASS, kSpecCoded}; // refers to itself
    EXPECT_EQ(fixture.format({0x00, 0x01, ELEMENT_TYPE_VOID, ELEMENT_TYPE_CLASS, kSpecCoded}), std::nullopt);

    // Each expansion doubles: List<List<...>, List<...>> would be 2^64 nodes without the output budget.
    fixture.names[kList] = "Pair`2";
    fixture.spec = {ELEMENT_TYPE_GENERICINST, ELEMENT_TYPE_CLASS, kListCoded, 0x02, ELEMENT_TYPE_CLASS, kSpecCoded,
                    ELEMENT_TYPE_CLASS, kSpecCoded};
    EXPECT_EQ(fixture.format({0x00, 0x01, ELEMENT_TYPE_VOID, ELEMENT_TYPE_CLASS, kSpecCoded}), std::nullopt);
}

TEST(Signature, ShortTypeNameStripsNamespaceAndArity) {
    EXPECT_EQ(signature::shortTypeName("System.Collections.Generic.Dictionary`2"), "Dictionary");
    EXPECT_EQ(signature::shortTypeName("Enumerator"), "Enumerator");
    EXPECT_EQ(signature::shortTypeName("Example.Weird`x"), "Weird`x");
    EXPECT_EQ(signature::shortTypeName("Trailing."), "Trailing.");
}
