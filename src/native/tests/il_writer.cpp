#include "sherlock/profiler/il_writer.hpp"

#include <gtest/gtest.h>

#include <vector>

using namespace Sherlock;

namespace {

constexpr BYTE kTypeDefCoded = (3 << 2) | 0;

// Skips one type and reports how many bytes it took; -1 when the blob is rejected.
int skipped(const std::vector<BYTE>& blob) {
    const BYTE* p = blob.data();
    if (!il::skipType(p, blob.data() + blob.size())) return -1;
    return static_cast<int>(p - blob.data());
}

} // namespace

TEST(SkipType, SkipsEveryKindOfType) {
    EXPECT_EQ(1, skipped({ELEMENT_TYPE_I4, ELEMENT_TYPE_I4}));
    EXPECT_EQ(2, skipped({ELEMENT_TYPE_CLASS, kTypeDefCoded}));
    EXPECT_EQ(2, skipped({ELEMENT_TYPE_SZARRAY, ELEMENT_TYPE_U1}));
    EXPECT_EQ(2, skipped({ELEMENT_TYPE_MVAR, 0}));
    EXPECT_EQ(2, skipped({ELEMENT_TYPE_PTR, ELEMENT_TYPE_VOID}));
    // List<KeyValuePair<string, T[]>>
    EXPECT_EQ(12, skipped({ELEMENT_TYPE_GENERICINST, ELEMENT_TYPE_CLASS, kTypeDefCoded, 1,
                           ELEMENT_TYPE_GENERICINST, ELEMENT_TYPE_VALUETYPE, kTypeDefCoded, 2,
                           ELEMENT_TYPE_STRING, ELEMENT_TYPE_SZARRAY, ELEMENT_TYPE_VAR, 0}));
    // int[0..2, 0..]: rank 2, one size, two lower bounds
    EXPECT_EQ(8, skipped({ELEMENT_TYPE_ARRAY, ELEMENT_TYPE_I4, 2, 1, 3, 2, 0, 0}));
    // delegate* unmanaged<int, void>
    EXPECT_EQ(5, skipped({ELEMENT_TYPE_FNPTR, IMAGE_CEE_CS_CALLCONV_C, 1, ELEMENT_TYPE_VOID, ELEMENT_TYPE_I4}));
    // modreq(IsVolatile) int
    EXPECT_EQ(3, skipped({ELEMENT_TYPE_CMOD_REQD, kTypeDefCoded, ELEMENT_TYPE_I4}));
}

TEST(SkipType, RejectsMalformedAndTruncatedTypes) {
    EXPECT_EQ(-1, skipped({}));
    EXPECT_EQ(-1, skipped({ELEMENT_TYPE_SZARRAY}));
    EXPECT_EQ(-1, skipped({ELEMENT_TYPE_GENERICINST, ELEMENT_TYPE_I4, 1, ELEMENT_TYPE_I4}));
    EXPECT_EQ(-1, skipped({ELEMENT_TYPE_GENERICINST, ELEMENT_TYPE_CLASS, kTypeDefCoded, 2, ELEMENT_TYPE_I4}));
    EXPECT_EQ(-1, skipped({ELEMENT_TYPE_ARRAY, ELEMENT_TYPE_I4, 2, 1}));
    EXPECT_EQ(-1, skipped({ELEMENT_TYPE_CMOD_OPT, kTypeDefCoded}));
    EXPECT_EQ(-1, skipped({0x7f}));
}

TEST(SkipType, RejectsImplausiblyDeepNesting) {
    std::vector<BYTE> deep(200, ELEMENT_TYPE_SZARRAY);
    deep.push_back(ELEMENT_TYPE_I4);
    EXPECT_EQ(-1, skipped(deep));

    std::vector<BYTE> shallow(10, ELEMENT_TYPE_SZARRAY);
    shallow.push_back(ELEMENT_TYPE_I4);
    EXPECT_EQ(11, skipped(shallow));
}
