# Provision or repair the governed SLH Customer Contacts list.
# Requires PnP.PowerShell and permission to manage the SLH Hub site.
param([Parameter(Mandatory=$true)][string]$SiteUrl)

$ErrorActionPreference = 'Stop'
Connect-PnPOnline -Url $SiteUrl -Interactive

$listName = 'Hub Customer Contacts'
$list = Get-PnPList -Identity $listName -ErrorAction SilentlyContinue
if(-not $list) { $list = New-PnPList -Title $listName -Template GenericList -OnQuickLaunch }
Set-PnPList -Identity $list -Description 'Governed customer communication contacts. Controls ETA recipient suggestions used by the TMS; SQL is only the operational projection.' -EnableVersioning $true -MajorVersions 50 | Out-Null

$fields = @(
 @{Name='ContactKey';Type='Text';Required=$true},
 @{Name='CustomerKey';Type='Text';Required=$true},
 @{Name='ContactName';Type='Text';Required=$true},
 @{Name='Email';Type='Text';Required=$false},
 @{Name='MobileNumber';Type='Text';Required=$false},
 @{Name='ReceivesEtaUpdates';Type='Boolean';Required=$false},
 @{Name='Active';Type='Boolean';Required=$false}
)

foreach($field in $fields) {
 $internal = $field.Name -replace '[^A-Za-z0-9]',''
 if(-not (Get-PnPField -List $list -Identity $internal -ErrorAction SilentlyContinue)) {
  $params = @{List=$list;DisplayName=$field.Name;InternalName=$internal;Type=$field.Type;AddToDefaultView=$true}
  if($field.Required){$params.Required=$true}
  Add-PnPField @params | Out-Null
 }
}

$viewTitle = 'Hub Customer Contacts - Active'
$view = Get-PnPView -List $list -Identity $viewTitle -ErrorAction SilentlyContinue
$viewFields = @('ContactKey','CustomerKey','ContactName','Email','MobileNumber','ReceivesEtaUpdates','Active')
$query = "<Where><Eq><FieldRef Name='Active'/><Value Type='Boolean'>1</Value></Eq></Where>"
if(-not $view) {
 Add-PnPView -List $list -Title $viewTitle -Fields $viewFields -Query $query -SetAsDefault -Paged -RowLimit 200 | Out-Null
} else {
 Set-PnPView -List $list -Identity $viewTitle -Fields $viewFields | Out-Null
}

Write-Host 'Hub Customer Contacts is provisioned and ready for TMS CRM sync.'
