define(['baseView', 'globalize', 'loading', 'appRouter', 'formHelper', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (BaseView, globalize, loading, appRouter, formHelper) {
    'use strict';

    var TunerType = 'xtream';

    function onBackClick() {
        appRouter.back();
    }

    function alertText(options) {
        require(['alert']).then(function (responses) {
            responses[0](options);
        });
    }

    function getTunerHostConfiguration(id) {
        if (id) {
            return ApiClient.getTunerHostConfiguration(id);
        }
        return ApiClient.getDefaultTunerHostConfiguration(TunerType);
    }

    function format(text, args) {
        for (var i = 0; i < args.length; i++) {
            text = text.replace('{' + i + '}', args[i]);
        }
        return text;
    }

    // ---- categories -----------------------------------------------------------

    function getSelectedCategories(view) {
        var list = [];
        var boxes = view.querySelectorAll('.chkCategory');
        for (var i = 0; i < boxes.length; i++) {
            if (boxes[i].checked) {
                list.push(boxes[i].getAttribute('data-id'));
            }
        }
        return list;
    }

    function renderCategories(view, categories, selected) {
        var container = view.querySelector('.categoryList');
        container.innerHTML = '';

        if (!categories.length) {
            var none = document.createElement('div');
            none.className = 'fieldDescription';
            none.textContent = globalize.translate('XtreamNoCategories');
            container.appendChild(none);
            return;
        }

        categories.forEach(function (category) {
            var wrapper = document.createElement('div');
            wrapper.className = 'checkboxContainer';

            var label = document.createElement('label');
            var input = document.createElement('input');
            input.setAttribute('is', 'emby-checkbox');
            input.type = 'checkbox';
            input.className = 'chkCategory';
            input.setAttribute('data-id', category.Id);
            input.checked = selected.indexOf(category.Id) !== -1;

            var span = document.createElement('span');
            span.textContent = category.Name;

            label.appendChild(input);
            label.appendChild(span);
            wrapper.appendChild(label);
            container.appendChild(wrapper);
        });
    }

    function loadCategories(view, selected) {
        var args = getConnectionArgs(view);
        if (!args.Url || !args.Username || !args.Password) {
            return Promise.resolve();
        }
        return ApiClient.getJSON(ApiClient.getUrl('Xtream/Categories', args)).then(function (categories) {
            renderCategories(view, categories || [], selected);
        }, function () {
            renderCategories(view, [], selected);
        });
    }

    // ---- connection -----------------------------------------------------------

    function getConnectionArgs(view) {
        return {
            Url: view.querySelector('.txtServerUrl').value.trim(),
            Username: view.querySelector('.txtUsername').value.trim(),
            Password: view.querySelector('.txtPassword').value,
            UserAgent: view.querySelector('.txtUserAgent').value.trim()
        };
    }

    // Splits http://host:port/get.php?username=U&password=P&type=m3u_plus into base URL + credentials.
    function splitM3uUrl(value) {
        var q = value.indexOf('?');
        if (q < 0) {
            return null;
        }
        var params = {};
        value.substring(q + 1).split('&').forEach(function (part) {
            var eq = part.indexOf('=');
            if (eq > 0) {
                try {
                    params[part.substring(0, eq).toLowerCase()] = decodeURIComponent(part.substring(eq + 1).replace(/\+/g, ' '));
                } catch (e) {
                    params[part.substring(0, eq).toLowerCase()] = part.substring(eq + 1);
                }
            }
        });
        if (!params.username || params.password == null) {
            return null;
        }
        var url = value.substring(0, q).replace(/\/(get|player_api|xmltv|panel_api)\.php$/i, '').replace(/\/+$/, '');
        return { url: url, username: params.username, password: params.password };
    }

    function onServerUrlChange() {
        var view = this.view;
        var input = view.querySelector('.txtServerUrl');
        var parts = splitM3uUrl(input.value.trim());
        if (parts) {
            input.value = parts.url;
            view.querySelector('.txtUsername').value = parts.username;
            view.querySelector('.txtPassword').value = parts.password;
        }
    }

    function onTestClick() {
        var view = this.view;
        var result = view.querySelector('.testResult');
        var args = getConnectionArgs(view);

        result.textContent = globalize.translate('XtreamTesting');

        ApiClient.getJSON(ApiClient.getUrl('Xtream/Test', args)).then(function (r) {
            if (r.Success) {
                result.textContent = format(globalize.translate('XtreamTestOk'), [
                    r.Status || '-',
                    r.ExpiryDate || globalize.translate('XtreamNever'),
                    r.MaxConnections
                ]);
                if (r.MaxConnections > 0) {
                    view.querySelector('.txtTunerCount').value = r.MaxConnections;
                }
                loadCategories(view, getSelectedCategories(view));
            } else {
                result.textContent = format(globalize.translate('XtreamTestFailed'), [r.Message || '']);
            }
        }, function () {
            result.textContent = format(globalize.translate('XtreamTestFailed'), ['-']);
        });
    }

    function updateVisibility(view) {
        var guide = view.querySelector('.chkImportGuideData').checked;
        view.querySelector('.fldGuideSource').classList.toggle('hide', !guide);

        var catchup = view.querySelector('.chkEnableCatchup').checked;
        view.querySelector('.fldCatchupMaxDays').classList.toggle('hide', !catchup);
        view.querySelector('.fldCatchupStyle').classList.toggle('hide', !catchup);
    }

    // ---- load / save ----------------------------------------------------------

    function fillTunerHostInfo(view, info) {
        var options = {};
        try {
            options = JSON.parse(info.ProviderOptions || '{}') || {};
        } catch (e) {
            options = {};
        }

        view.querySelector('.txtFriendlyName').value = info.FriendlyName || '';
        view.querySelector('.txtServerUrl').value = info.Url || '';
        view.querySelector('.txtUsername').value = options.Username || '';
        view.querySelector('.txtPassword').value = options.Password || '';
        view.querySelector('.selectStreamFormat').value = options.StreamFormat || 'ts';
        view.querySelector('.chkImportGuideData').checked = options.ImportGuideData !== false;
        view.querySelector('.selectGuideSource').value = options.GuideSource || 'xmltv';
        view.querySelector('.chkEnableCatchup').checked = options.EnableCatchup !== false;
        view.querySelector('.txtCatchupMaxDays').value = options.CatchupMaxDays || 0;
        view.querySelector('.selectCatchupStyle').value = options.CatchupUrlStyle || 'query';
        view.querySelector('.txtTunerCount').value = info.TunerCount == null ? 1 : info.TunerCount;
        view.querySelector('.txtUserAgent').value = info.UserAgent || '';

        updateVisibility(view);
        return options.Categories || [];
    }

    function reload(instance) {
        var view = instance.view;
        getTunerHostConfiguration(instance.params.id).then(function (info) {
            var selected = fillTunerHostInfo(view, info);
            renderCategories(view, [], selected);
            if (instance.params.id) {
                loadCategories(view, selected);
            }
        });
    }

    function submitForm(instance) {
        loading.show();

        var params = instance.params;
        var view = instance.view;

        // a pasted M3U link may not have triggered the change event yet
        onServerUrlChange.call(instance);

        getTunerHostConfiguration(params.id).then(function (info) {

            var url = view.querySelector('.txtServerUrl').value.trim();

            info.Type = TunerType;
            info.FriendlyName = view.querySelector('.txtFriendlyName').value || null;
            info.Url = url || null;
            info.UserAgent = view.querySelector('.txtUserAgent').value.trim() || null;
            info.TunerCount = parseInt(view.querySelector('.txtTunerCount').value, 10) || 0;
            info.ImportGuideData = view.querySelector('.chkImportGuideData').checked;

            var options = {};
            try {
                options = JSON.parse(info.ProviderOptions || '{}') || {};
            } catch (e) {
                options = {};
            }

            options.Username = view.querySelector('.txtUsername').value.trim();
            options.Password = view.querySelector('.txtPassword').value;
            options.StreamFormat = view.querySelector('.selectStreamFormat').value;

            // when categories were never loaded keep what was saved, otherwise take the ticked boxes
            if (view.querySelectorAll('.chkCategory').length) {
                options.Categories = getSelectedCategories(view);
            } else {
                options.Categories = options.Categories || [];
            }

            options.ImportGuideData = info.ImportGuideData;
            options.GuideSource = view.querySelector('.selectGuideSource').value;
            options.EnableCatchup = view.querySelector('.chkEnableCatchup').checked;
            options.CatchupMaxDays = parseInt(view.querySelector('.txtCatchupMaxDays').value, 10) || 0;
            options.CatchupUrlStyle = view.querySelector('.selectCatchupStyle').value;

            info.ProviderOptions = JSON.stringify(options);

            ApiClient.saveTunerHostConfiguration(info).then(function (result) {

                formHelper.handleConfigurationSavedResponse();

                appRouter.show(appRouter.getRouteUrl('LiveTVSetup', {
                    SavedTunerHostId: (result || {}).Id || info.Id,
                    IsNew: params.id == null
                }));

            }, function () {
                loading.hide();

                alertText({
                    text: globalize.translate('ErrorSavingTvProvider')
                });
            });
        });
    }

    function onSubmit(e) {
        e.preventDefault();
        e.stopPropagation();
        submitForm(this);
        return false;
    }

    function View(view, params) {
        BaseView.apply(this, arguments);

        view.querySelector('.btnCancel').addEventListener('click', onBackClick);
        view.querySelector('form').addEventListener('submit', onSubmit.bind(this));
        view.querySelector('.btnTest').addEventListener('click', onTestClick.bind(this));
        view.querySelector('.txtServerUrl').addEventListener('change', onServerUrlChange.bind(this));
        view.querySelector('.chkImportGuideData').addEventListener('change', function () { updateVisibility(view); });
        view.querySelector('.chkEnableCatchup').addEventListener('change', function () { updateVisibility(view); });
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        reload(this);
    };

    return View;
});
