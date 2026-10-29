# Third-party notices

## AdventureWorks sample database

The database schema, stored procedures, triggers and data this lab runs on are **Microsoft's
AdventureWorks sample**, from `microsoft/sql-server-samples`
(<https://github.com/microsoft/sql-server-samples>). The schema is Microsoft's. It is not a
client system and does not represent one. Its person and employee rows are fictional.

The backup `AdventureWorks2022.bak` is downloaded by `scripts/get_adventureworks.py` and is never
committed to this repository. The licence of the sample code is:

```text
Microsoft SQL Server Sample Code

Copyright (c) Microsoft Corporation

All rights reserved.

MIT License.

Permission is hereby granted, free of charge, to any person obtaining a copy
 of this software and associated documentation files (the "Software"), to deal
 in the Software without restriction, including without limitation the rights
 to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 copies of the Software, and to permit persons to whom the Software is
 furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
 all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 THE SOFTWARE.
```

## SQL Server engine

The SQL Server container image (`mcr.microsoft.com/mssql/server:2022-latest`) is **not** MIT. It
runs here as Developer edition under Microsoft's licence terms, which allow development and test
use only. Accepting them is the `ACCEPT_EULA=Y` setting in `docker-compose.yml`.
