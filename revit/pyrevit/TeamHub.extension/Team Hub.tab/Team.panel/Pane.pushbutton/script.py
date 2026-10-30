# -*- coding: utf-8 -*-
from pyrevit import HOST_APP
from teamhub_load import engine

result = engine().PyRevit.Run('pane', HOST_APP.uiapp)
if result not in ("Succeeded", "Cancelled"):
    print(result)
