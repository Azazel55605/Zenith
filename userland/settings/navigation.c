#include "navigation.h"
/* Reviewed category names in the inventory, and the shorter sidebar labels. */
static const char *const inventory[10]={"Quick Settings","Input & Output","Connected Devices","Networking","Appearance & Style",
    "Apps & Windows","Workspace","Security & Privacy","Language & Time","System"};
const char *const meridian_categories[10]={"Quick Settings","Input & Output","Devices","Networking","Appearance",
    "Apps & Windows","Workspace","Security","Language & Time","System"};
const char *const meridian_category_icons[10]={"zap","keyboard","usb","network","palette","app-window","layout-grid",
    "shield","languages","monitor-cog"};
static const char *const groups[7]={"Mouse & Touchpad","Keyboard","Sound","Display & Monitor","Colors & Themes","Text & Fonts","Window Management"};
static int same(const char *a,const char *b){while(*a&&*a==*b){a++;b++;}return *a==*b;}
static char lower(char c){return c>='A'&&c<='Z'?(char)(c+32):c;}
static int contains(const char *s,const char *q){
    if(!*q)return 1;
    for(;*s;s++){const char *a=s,*b=q;while(*a&&*b&&lower(*a)==lower(*b)){a++;b++;}if(!*b)return 1;}
    return 0;
}
int nav_category_of(int page){
    for(int i=0;i<10;i++)if(same(inventory[i],meridian_pages[page].category))return i;
    return 0;
}
int nav_category_pages(int category,int *out){
    int n=0;
    for(int i=0;i<MERIDIAN_PAGE_COUNT;i++)if(nav_category_of(i)==category)out[n++]=i;
    return n;
}
static int direct(int category){int pages[MERIDIAN_PAGE_COUNT];return nav_category_pages(category,pages)==1;}
void nav_init(struct meridian_nav *n,int page){
    *n=(struct meridian_nav){0};
    n->page=page>=0&&page<MERIDIAN_PAGE_COUNT?page:0;n->category=nav_category_of(n->page);
    n->mode=direct(n->category)?NAV_CATEGORIES:NAV_PAGES;n->previous_mode=n->mode;
}
int nav_row_count(const struct meridian_nav *n){
    if(n->mode==NAV_CATEGORIES)return 10;
    if(n->mode==NAV_SEARCH)return 1+n->result_count;
    int pages[MERIDIAN_PAGE_COUNT];return 1+nav_category_pages(n->category,pages);
}
int nav_row_page(const struct meridian_nav *n,int index){
    if(index<0||index>=nav_row_count(n))return -1;
    if(n->mode==NAV_CATEGORIES){int pages[MERIDIAN_PAGE_COUNT];return nav_category_pages(index,pages)==1?pages[0]:-1;}
    if(!index)return -1;
    if(n->mode==NAV_SEARCH)return n->results[index-1];
    int pages[MERIDIAN_PAGE_COUNT];nav_category_pages(n->category,pages);return pages[index-1];
}
int nav_rows(const struct meridian_nav *n,int index,struct zui_list_item *out){
    if(index<0||index>=nav_row_count(n))return 0;
    if(n->mode==NAV_CATEGORIES){
        *out=(struct zui_list_item){meridian_category_icons[index],meridian_categories[index],0,!direct(index),1};return 1;
    }
    if(!index){
        *out=(struct zui_list_item){"arrow-left",n->mode==NAV_SEARCH?"All settings":meridian_categories[n->category],0,0,1};return 1;
    }
    int page=nav_row_page(n,index);const struct meridian_page *p=&meridian_pages[page];
    const char *subtitle=n->mode==NAV_SEARCH?meridian_categories[nav_category_of(page)]:p->group>=0&&p->group<7?groups[p->group]:0;
    *out=(struct zui_list_item){0,p->title,subtitle,0,1};return 1;
}
void nav_activate(struct meridian_nav *n,int row){
    if(row<0||row>=nav_row_count(n))return;
    if(n->mode==NAV_CATEGORIES){
        int pages[MERIDIAN_PAGE_COUNT],count=nav_category_pages(row,pages);
        n->category=row;n->page=pages[0];if(count>1)n->mode=NAV_PAGES;
        return;
    }
    if(!row){if(n->mode==NAV_SEARCH)nav_search(n,"");else nav_back(n);return;}
    n->page=nav_row_page(n,row);
}
void nav_back(struct meridian_nav *n){n->mode=NAV_CATEGORIES;n->previous_mode=NAV_CATEGORIES;n->query[0]=0;n->result_count=0;}
void nav_search(struct meridian_nav *n,const char *query){
    unsigned long i=0;
    for(;query[i]&&i<sizeof(n->query)-1;i++)n->query[i]=query[i];
    n->query[i]=0;
    if(!n->query[0]){if(n->mode==NAV_SEARCH)n->mode=n->previous_mode;n->result_count=0;return;}
    if(n->mode!=NAV_SEARCH)n->previous_mode=n->mode;
    n->mode=NAV_SEARCH;n->result_count=0;
    for(int p=0;p<MERIDIAN_PAGE_COUNT;p++){
        const struct meridian_page *page=&meridian_pages[p];
        if(contains(page->title,n->query)||contains(page->id,n->query)||contains(page->requirements,n->query))n->results[n->result_count++]=p;
    }
}
int nav_open(struct meridian_nav *n,const char *id){
    for(int i=0;i<MERIDIAN_PAGE_COUNT;i++)if(same(id,meridian_pages[i].id)){
        n->page=i;n->category=nav_category_of(i);n->mode=direct(n->category)?NAV_CATEGORIES:NAV_PAGES;
        n->previous_mode=n->mode;n->query[0]=0;n->result_count=0;return 1;
    }
    return 0;
}
