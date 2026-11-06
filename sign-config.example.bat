@echo off
rem Copy this file to sign-config.bat (it is not committed: see .gitignore) and fill it in.
rem The certificate must be visible in "certmgr.msc -> Personal -> Certificates"
rem (for Certum: insert the card, proCertum CardManager registers the certificate there).

rem Either the certificate thumbprint (preferred, exact):
set "SIGN_THUMBPRINT="
rem ...or the subject name as shown in the certificate (e.g. Open Source Developer, Vadym ...):
set "SIGN_SUBJECT="

rem Timestamp server of your certificate authority (signature stays valid after the certificate expires)
set "SIGN_TIMESTAMP=http://time.certum.pl"

rem Full path to signtool.exe; empty = search the Windows SDK automatically
set "SIGNTOOL="
