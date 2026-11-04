#include <opencc/opencc.h>

opencc_t lyricify_opencc_open(const char *config)
{
    return opencc_open(config);
}

char *lyricify_opencc_convert(opencc_t converter, const char *text)
{
    return opencc_convert_utf8(converter, text, (size_t)-1);
}

void lyricify_opencc_free(char *text)
{
    opencc_convert_utf8_free(text);
}
