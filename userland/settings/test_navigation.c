/* Host tests for Meridian's drawing-free navigation model. */
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "navigation.h"
#include "bindings.h"
static int find(const char *id){for(int i=0;i<MERIDIAN_PAGE_COUNT;i++)if(!strcmp(meridian_pages[i].id,id))return i;assert(0);return -1;}
static int row_of_page(const struct meridian_nav *n,int page){
    for(int i=0;i<nav_row_count(n);i++)if(nav_row_page(n,i)==page)return i;
    return -1;
}
static struct zenith_system_settings prefs;
static int prefs_dirty,prefs_saves;
static long prefs_read(struct zenith_system_settings *s){*s=prefs;return 0;}
static long prefs_save(unsigned int field,unsigned int choice,unsigned long revision){
    if(!(prefs.flags&2))return -13;
    if(revision!=prefs.revision)return -11;
    if(field==1)prefs.keyboard=choice;else if(field==2){prefs.zone=choice;strcpy(prefs.current_zone,prefs.zones[choice]);}
    else{prefs.session=choice;strcpy(prefs.current_session,prefs.sessions[choice]);}
    prefs.revision++;prefs_saves++;return 0;
}
static long prefs_mark(int d){prefs_dirty=d;return 0;}
static struct zenith_appearance look;
static long look_read(struct zenith_appearance *s,unsigned int *m,unsigned int *e,unsigned int *v,unsigned int *r,unsigned int *l){*s=look;*m=*e=0;*v=7;*r=0;*l=40;return 0;}
static long look_save(unsigned int m,unsigned int a,unsigned int e,unsigned int l,unsigned long rev){(void)e;(void)l;if(rev!=look.revision)return -11;look.mode=m;look.accent=a;look.revision++;return 0;}
static unsigned int pixels[600*480];
static void binding_tests(void){
    prefs=(struct zenith_system_settings){.version=1,.flags=3,.revision=1,.keyboard_count=2,.zone_count=2,.current_zone="UTC",.keyboards={"us","de"},.zones={"UTC","Europe/Berlin"}};
    look=(struct zenith_appearance){.version=1,.flags=3,.revision=1};
    static struct settings_app colors;assert(settings_init(&colors,pixels,(struct settings_backend){look_read,look_save,prefs_mark,0}));
    struct meridian_bindings b;binding_init(&b,&colors,prefs_read,prefs_save,prefs_mark);
    assert(binding_of("style.colors")==BIND_COLORS&&binding_of("input.keyboard")==BIND_KEYBOARD&&binding_of("locale.time")==BIND_TIMEZONE);
    assert(binding_of("system.session")==BIND_SESSION&&binding_of("devices.bluetooth")==BIND_NONE);
    assert(binding_has_defaults(BIND_COLORS)&&!binding_has_defaults(BIND_KEYBOARD)&&binding_at_defaults(&b,BIND_KEYBOARD));
    assert(binding_choice_count(&b,BIND_KEYBOARD)==2&&!strcmp(binding_choice_name(&b,BIND_KEYBOARD,1),"de"));
    assert(binding_choice_count(&b,BIND_SESSION)==0);                      /* version 1 has no sessions */
    assert(!binding_choose(&b,BIND_KEYBOARD,1)&&binding_changed(&b,BIND_KEYBOARD)&&prefs_dirty);
    assert(binding_choose(&b,BIND_KEYBOARD,9)==-22);
    assert(!binding_apply(&b,BIND_KEYBOARD)&&prefs.keyboard==1&&!binding_changed(&b,BIND_KEYBOARD)&&!prefs_dirty);
    assert(!binding_choose(&b,BIND_TIMEZONE,1)&&binding_changed(&b,BIND_TIMEZONE));
    prefs.revision++;binding_refresh(&b);assert(binding_changed(&b,BIND_TIMEZONE)&&b.message);
    assert(binding_apply(&b,BIND_TIMEZONE)==-11&&binding_changed(&b,BIND_TIMEZONE)&&prefs.zone==0);
    binding_reset(&b,BIND_TIMEZONE);assert(!binding_changed(&b,BIND_TIMEZONE)&&!prefs_dirty&&!b.message);
    assert(!binding_choose(&b,BIND_TIMEZONE,1)&&!binding_apply(&b,BIND_TIMEZONE)&&prefs.zone==1);
    prefs.flags=1;binding_refresh(&b);assert(binding_choose(&b,BIND_KEYBOARD,0)==-13&&!binding_changed(&b,BIND_KEYBOARD));
    prefs.flags=3;binding_refresh(&b);
    assert(!settings_preview(&colors,1,3,0,40)&&binding_changed(&b,BIND_COLORS)&&prefs_dirty);
    assert(!binding_at_defaults(&b,BIND_COLORS));binding_reset(&b,BIND_COLORS);assert(!binding_changed(&b,BIND_COLORS));
    assert(!settings_preview(&colors,1,3,0,40)&&!binding_apply(&b,BIND_COLORS)&&look.mode==1&&look.accent==3);
    binding_defaults(&b,BIND_COLORS);assert(binding_changed(&b,BIND_COLORS)&&binding_at_defaults(&b,BIND_COLORS));
    binding_reset(&b,BIND_COLORS);
    prefs.version=2;prefs.session_count=2;strcpy(prefs.sessions[0],"de");strcpy(prefs.sessions[1],"headless");strcpy(prefs.current_session,"de");
    binding_refresh(&b);assert(binding_choice_count(&b,BIND_SESSION)==2);
    assert(!binding_choose(&b,BIND_SESSION,1)&&!binding_apply(&b,BIND_SESSION)&&prefs.session==1&&prefs_saves==3);
    prefs.keyboard_count=99;binding_refresh(&b);assert(!b.ready&&b.message);  /* malformed service data */
}
int main(void){
    binding_tests();
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
    puts("PASS Meridian navigation and page bindings");
    return 0;
}
