# -*- coding: utf-8 -*-
"""Team Hub: start the live engine when Revit (pyRevit) starts."""
from pyrevit import HOST_APP
from teamhub_load import engine

result = engine().App.StartFromPyRevit(HOST_APP.uiapp)
print('Team Hub: {}'.format(result))
