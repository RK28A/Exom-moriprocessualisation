#include <stdio.h>
__attribute__((constructor)) static void main_init(void) { fprintf(stderr, "[fake libmain] constructor\n"); }
int fake_main_entry(void) { return 42; }
