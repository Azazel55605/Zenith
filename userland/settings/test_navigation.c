/* Host tests for Meridian's drawing-free navigation model. */
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "navigation.h"
static int find(const char *id){for(int i=0;i<MERIDIAN_PAGE_COUNT;i++)if(!strcmp(meridian_pages[i].id,id))return i;assert(0);return -1;}
static int row_of_page(const struct meridian_nav *n,int page){
    for(int i=0;i<nav_row_count(n);i++)if(nav_row_page(n,i)==page)return i;
    return -1;
}
int main(void){
    struct meridian_nav n;nav_init(&n,0);
    assert(n.mode==NAV_CATEGORIES&&n.page==find("quick.overview")&&nav_row_count(&n)==10);
    /* every page appears in exactly one category list */
    int seen[MERIDIAN_PAGE_COUNT]={0},pages[MERIDIAN_PAGE_COUNT];
    for(int c=0;c<10;c++){int k=nav_category_pages(c,pages);assert(k>0);for(int i=0;i<k;i++)seen[pages[i]]++;}
    for(int i=0;i<MERIDIAN_PAGE_COUNT;i++)assert(seen[i]==1);
    struct zui_list_item it;
    assert(nav_rows(&n,1,&it)&&!strcmp(it.label,"Input & Output")&&!strcmp(it.icon,"keyboard")&&it.chevron);
    assert(nav_rows(&n,0,&it)&&!it.chevron);                         /* Quick Settings has no sub-pages */
    nav_activate(&n,0);assert(n.mode==NAV_CATEGORIES&&n.page==find("quick.overview"));
    nav_activate(&n,1);assert(n.mode==NAV_PAGES&&n.category==1&&n.page==find("input.mouse"));
    assert(nav_row_count(&n)==1+10);                                  /* back row + ten pages */
    assert(nav_rows(&n,0,&it)&&!strcmp(it.icon,"arrow-left")&&nav_row_page(&n,0)<0);
    assert(nav_rows(&n,row_of_page(&n,find("input.mouse")),&it)&&it.subtitle&&!strcmp(it.subtitle,"Mouse & Touchpad"));
    nav_activate(&n,row_of_page(&n,find("input.keyboard")));assert(n.page==find("input.keyboard")&&n.mode==NAV_PAGES);
    nav_activate(&n,0);assert(n.mode==NAV_CATEGORIES&&n.category==1&&n.page==find("input.keyboard"));
    nav_activate(&n,1);nav_back(&n);assert(n.mode==NAV_CATEGORIES&&n.category==1);
    nav_search(&n,"bluetooth");assert(n.mode==NAV_SEARCH&&n.result_count==1&&n.results[0]==find("devices.bluetooth"));
    assert(nav_rows(&n,1,&it)&&!strcmp(it.label,"Bluetooth")&&!strcmp(it.subtitle,"Devices"));
    nav_activate(&n,1);assert(n.page==find("devices.bluetooth")&&n.mode==NAV_SEARCH);
    nav_search(&n,"");assert(n.mode==NAV_CATEGORIES);
    nav_search(&n,"SCROLL SPEED");assert(n.result_count==2);           /* labels, case-insensitive */
    nav_search(&n,"zzzz-no-match");assert(n.mode==NAV_SEARCH&&n.result_count==0&&nav_row_count(&n)==1);
    nav_back(&n);assert(n.mode==NAV_CATEGORIES&&!n.query[0]);
    for(int i=0;i<MERIDIAN_PAGE_COUNT;i++){
        assert(nav_open(&n,meridian_pages[i].id)&&n.page==i);
        int direct=nav_category_pages(n.category,pages)==1;      /* Quick Settings, Networking, Security */
        assert(n.mode==(direct?NAV_CATEGORIES:NAV_PAGES)&&row_of_page(&n,i)>=0);
    }
    int before=n.page;assert(!nav_open(&n,"missing.page")&&n.page==before);
    char long_query[200];memset(long_query,'a',199);long_query[199]=0;nav_search(&n,long_query);assert(strlen(n.query)<sizeof(n.query));
    puts("PASS Meridian navigation: drill-down, back, search, open and complete coverage");
    return 0;
}
