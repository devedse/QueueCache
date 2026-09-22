// Compile-time model of the usage-path/activation ordering contract.
#pragma once

struct QC_USAGE_PATH_MODEL
{
    bool Enabled;
    unsigned Paths;
};

constexpr bool QcModelEnable(QC_USAGE_PATH_MODEL& state)
{
    if (state.Paths != 0)
        return false;
    state.Enabled = true;
    return true;
}

constexpr void QcModelReserveInPath(QC_USAGE_PATH_MODEL& state)
{
    ++state.Paths;
}

constexpr void QcModelCompleteInPath(QC_USAGE_PATH_MODEL& state, bool success)
{
    if (!success)
        --state.Paths;
    else
        state.Enabled = false;
}

constexpr void QcModelCompleteOutPath(QC_USAGE_PATH_MODEL& state, bool success)
{
    if (success && state.Paths != 0)
        --state.Paths;
}

constexpr bool QcUsagePathContract()
{
    QC_USAGE_PATH_MODEL notificationFirst{false, 0};
    QcModelReserveInPath(notificationFirst);
    if (QcModelEnable(notificationFirst))
        return false;
    QcModelCompleteInPath(notificationFirst, true);
    if (notificationFirst.Enabled || notificationFirst.Paths != 1)
        return false;
    QcModelCompleteOutPath(notificationFirst, false);
    if (notificationFirst.Paths != 1)
        return false;
    QcModelCompleteOutPath(notificationFirst, true);
    if (notificationFirst.Paths != 0)
        return false;

    QC_USAGE_PATH_MODEL enableFirst{false, 0};
    if (!QcModelEnable(enableFirst))
        return false;
    QcModelReserveInPath(enableFirst);
    QcModelCompleteInPath(enableFirst, true);
    if (enableFirst.Enabled || enableFirst.Paths != 1)
        return false;

    QC_USAGE_PATH_MODEL failedNotification{false, 0};
    QcModelReserveInPath(failedNotification);
    QcModelCompleteInPath(failedNotification, false);
    return failedNotification.Paths == 0 && QcModelEnable(failedNotification);
}

static_assert(QcUsagePathContract(), "Usage-path reservation/activation contract changed");
