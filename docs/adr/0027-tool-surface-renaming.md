# Tool surface renaming: verbs first, no shape suffixes, one finance write

Status: accepted

Read monthly income, expenses and balance with `get_budget`. Select
`kind=tax|fee|service|loan` for the current values and limits in one
part of the budget. Change a value with `set_budget` using the same
kind. Budget reads stay available with treasury changes disabled.
Returning one view per call keeps recurring monthly checks compact.

Reads use `get_` for scalars and `list_` for collections; writes use one
verb per domain. `zone_area` (circle) is deleted and `zone_rectangle`
becomes `zone`: a lone zoning tool needs no shape suffix. `build_road`
becomes `build_network` because it also builds pipes and cables.
`set_tax`, `set_fee`, `set_service_budget` and `set_loan` merge into one
`set_budget(kind=tax|fee|service|loan, name, value)`: four one-line
sliders behind one validation switch. `set_policy` stays separate:
ordinances toggle on/off through a different native pipeline, while the
four finance sliders all move money. `create/delete_transit_line`
become `add/remove_transit_line` (lines are linked, not placed or
bulldozed); `inspect` becomes `inspect_entity` and
`get_operational_area` becomes `inspect_operational_area` to match;
`count_zone_cells` becomes `get_zone_counts`; `probe_cell_layer`
becomes `probe_layer` (cellmap is storage, not model vocabulary).

This beat keeping aliases: the product has not shipped, so unknown
shapes are rejected and old names are deleted from code, catalog,
routes and prompt alike. The prompt reflects only the current surface.
