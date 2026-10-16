#include "sherlock/profiler/utf8.hpp"

#include <gtest/gtest.h>

#include <vector>

using namespace Sherlock;

namespace {

std::optional<std::string> convert(std::vector<WCHAR> units, bool replaceInvalid = false) {
    return utf8(std::span<const WCHAR>(units), replaceInvalid);
}

} // namespace

TEST(Utf8, ConvertsEveryPlane) {
    EXPECT_EQ(convert({u'A', u'b'}), "Ab");
    EXPECT_EQ(convert({u'\u00dc', u'\u00f1'}), "\xc3\x9c\xc3\xb1");          // Üñ
    EXPECT_EQ(convert({u'\u7c7b', u'\u578b'}), "\xe7\xb1\xbb\xe5\x9e\x8b");  // 类型
    EXPECT_EQ(convert({0xd835, 0xdc9c}), "\xf0\x9d\x92\x9c");                 // U+1D49C
    EXPECT_EQ(convert({}), "");
}

TEST(Utf8, LoneSurrogatesAreInvalidUnlessReplaced) {
    EXPECT_EQ(convert({u'a', 0xd800}), std::nullopt);
    EXPECT_EQ(convert({0xdc00, u'a'}), std::nullopt);
    EXPECT_EQ(convert({u'a', 0xd800, u'b'}, true), "a\xef\xbf\xbd" "b");
    EXPECT_EQ(convert({0xdc00}, true), "\xef\xbf\xbd");
}
