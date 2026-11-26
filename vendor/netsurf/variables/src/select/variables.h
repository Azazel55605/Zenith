/* CSS custom properties. MIT license, as for the surrounding LibCSS code. */
#ifndef css_select_variables_h_
#define css_select_variables_h_

#include "stylesheet.h"
#include "parse/language.h"
#include "lex/lex.h"
#include "select/select.h"

struct css_var_tokens {
	css_token *items;
	size_t count, capacity;
};
struct css_var_decl {
	lwc_string *property;
	struct css_var_tokens value;
	size_t offset;
	bool custom, important;
	struct css_var_decl *next;
};

bool css_vars_name(lwc_string *name);
bool css_vars_used(const parserutils_vector *v, int32_t pos);
css_error css_vars_defer(css_language *c, const css_token *property,
		const parserutils_vector *v, int32_t *ctx, css_rule *rule, bool custom);
void css_vars_free_decls(struct css_var_decl *decl);
void css_vars_merge(css_style *target, css_style *from);
css_error css_vars_collect(css_select_state *state, const css_style *style);
css_error css_vars_finish(css_select_state *state, struct css_var_map *parent);
void css_vars_unref(struct css_var_map *map);
bool css_vars_may_revert(const struct css_var_map *map);
css_error css_vars_compile(css_stylesheet *sheet, const struct css_var_decl *decl,
		struct css_var_map *map, css_style **result);
css_error css__parse_expanded(css_stylesheet *sheet, lwc_string *property,
		const parserutils_vector *v, css_style **compiled);
#endif
