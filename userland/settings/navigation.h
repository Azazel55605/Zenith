#ifndef ZENITH_MERIDIAN_NAVIGATION_H
#define ZENITH_MERIDIAN_NAVIGATION_H
#include "../ui/widgets.h"
/* Generated page inventory (tools/build-meridian-pages.py). category is the
 * reviewed category name; requirements lists the planned options. */
#define MERIDIAN_PAGE_COUNT 43
enum { MERIDIAN_COLORS=16, MERIDIAN_ABOUT=38 };
struct meridian_page { const char *id,*title,*category,*requirements,*foundation; int group; };
extern const struct meridian_page meridian_pages[MERIDIAN_PAGE_COUNT];

/* Drawing-free sidebar model: the KDE-style category list, one category's pages
 * (with a back row first) or search results (with a back row first). page is the
 * open destination and is always valid. */
enum { NAV_CATEGORIES=0,NAV_PAGES=1,NAV_SEARCH=2 };
struct meridian_nav {
    int mode,previous_mode,category,page;
    int results[MERIDIAN_PAGE_COUNT],result_count;
    char query[64];
};
extern const char *const meridian_categories[10],*const meridian_category_icons[10];
void nav_init(struct meridian_nav *n,int page);
int nav_category_of(int page);
int nav_category_pages(int category,int *out);
int nav_row_count(const struct meridian_nav *n);
int nav_rows(const struct meridian_nav *n,int index,struct zui_list_item *out);
/* Page opened by a row, or -1 for back rows and categories with sub-pages. */
int nav_row_page(const struct meridian_nav *n,int index);
void nav_activate(struct meridian_nav *n,int row);
void nav_back(struct meridian_nav *n);
void nav_search(struct meridian_nav *n,const char *query);
int nav_open(struct meridian_nav *n,const char *id);
#endif
