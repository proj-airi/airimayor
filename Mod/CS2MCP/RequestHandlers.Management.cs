using System;
using System.Collections.Generic;
using Game.City;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MCP
{
    /// <summary>
    /// Management endpoints: loans (borrow/repay) and service fees
    /// (electricity/water/education... pricing).
    /// </summary>
    public sealed partial class RequestHandlers
    {
        private EntityQuery m_ServiceFeeParameterQuery;
        private bool m_ServiceFeeParameterQueryCreated;

        private EntityQuery ServiceFeeParameterQuery
        {
            get
            {
                if (!m_ServiceFeeParameterQueryCreated)
                {
                    m_ServiceFeeParameterQuery = EntityManager.CreateEntityQuery(new EntityQueryDesc
                    {
                        All = new[] { ComponentType.ReadOnly<ServiceFeeParameterData>() },
                        Options = EntityQueryOptions.IncludeSystems,
                    });
                    m_ServiceFeeParameterQueryCreated = true;
                }
                return m_ServiceFeeParameterQuery;
            }
        }

        private BridgeResponse ReadLoan()
        {
            LoanSystem loans = World.GetOrCreateSystemManaged<LoanSystem>();
            LoanInfo current = loans.CurrentLoan;
            return BridgeResponse.Json(new
            {
                currentLoan = new
                {
                    amount = current.m_Amount,
                    dailyInterestRate = current.m_DailyInterestRate,
                    dailyPayment = current.m_DailyPayment,
                },
                creditworthiness = loans.Creditworthiness,
                note = "set the loan principal with set_budget(kind=loan, value=N) (0 repays fully, max = creditworthiness)",
            });
        }

        private BridgeResponse SetLoan(BridgeRequest request)
        {
            if (!TryGetCity(out _, out BridgeResponse error))
            {
                return error;
            }
            if (!request.TryGetFloat("value", out float amountFloat))
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "provide ?value=<new loan principal; 0 repays fully>");
            }
            int amount = (int)amountFloat;
            LoanSystem loans = World.GetOrCreateSystemManaged<LoanSystem>();
            int applied = math.clamp(amount, 0, loans.Creditworthiness);
            LoanInfo offer = loans.RequestLoanOffer(applied);
            loans.ChangeLoan(applied);
            LoanInfo current = loans.CurrentLoan;
            return BridgeResponse.Json(new
            {
                requestedAmount = amount,
                appliedAmount = applied,
                offer = new { offer.m_Amount, offer.m_DailyInterestRate, offer.m_DailyPayment },
                currentLoan = new { current.m_Amount, current.m_DailyInterestRate, current.m_DailyPayment },
            });
        }

        private BridgeResponse ReadServiceFees(Entity city)
        {
            if (!TryGetServiceFeeParameters(out ServiceFeeParameterData feeParameters, out BridgeResponse parameterError))
            {
                return parameterError;
            }
            ServiceFeeSystem feeSystem = World.GetOrCreateSystemManaged<ServiceFeeSystem>();
            DynamicBuffer<ServiceFee> fees = EntityManager.GetBuffer<ServiceFee>(city, isReadOnly: true);
            var result = new Dictionary<string, object>();
            foreach (PlayerResource resource in Enum.GetValues(typeof(PlayerResource)))
            {
                if ((int)resource < 0)
                {
                    continue;
                }
                if (ServiceFeeSystem.TryGetFee(resource, fees, out float fee))
                {
                    FeeParameters slider = feeParameters.GetFeeParameters(resource);
                    result[resource.ToString()] = new
                    {
                        fee,
                        estimatedMonthlyIncome = feeSystem.GetServiceFeeIncomeEstimate(resource, fee),
                        adjustable = slider.m_Adjustable,
                        sliderRange = new { min = 0f, max = slider.m_Max, defaultValue = slider.m_Default },
                    };
                }
            }
            return BridgeResponse.Json(new
            {
                note = "set with set_budget(kind=fee, name=<resource>, value=<float>); fees affect service income and citizen happiness",
                fees = result,
            });
        }

        private bool TryGetServiceFeeParameters(out ServiceFeeParameterData feeParameters, out BridgeResponse error)
        {
            if (ServiceFeeParameterQuery.IsEmpty)
            {
                feeParameters = default;
                error = BridgeResponse.Error(BridgeErrorKind.Unavailable, "city fee settings are not loaded");
                return false;
            }
            feeParameters = ServiceFeeParameterQuery.GetSingleton<ServiceFeeParameterData>();
            error = null;
            return true;
        }

        private BridgeResponse SetFee(BridgeRequest request)
        {
            if (!TryGetCity(out Entity city, out BridgeResponse error))
            {
                return error;
            }
            if (!request.Query.TryGetValue("name", out string resourceName)
                || !Enum.TryParse(resourceName, ignoreCase: true, out PlayerResource resource))
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments,
                    $"provide ?name=<{string.Join("|", Enum.GetNames(typeof(PlayerResource)))}>");
            }
            if (!request.TryGetFloat("value", out float fee))
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, "provide ?value=<float>");
            }
            DynamicBuffer<ServiceFee> fees = EntityManager.GetBuffer<ServiceFee>(city);
            if (!ServiceFeeSystem.TryGetFee(resource, fees, out float previous))
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, $"resource '{resource}' has no adjustable fee in this city");
            }
            if (!TryGetServiceFeeParameters(out ServiceFeeParameterData feeParameters, out BridgeResponse parameterError))
            {
                return parameterError;
            }
            FeeParameters slider = feeParameters.GetFeeParameters(resource);
            if (!slider.m_Adjustable)
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments, $"{resource} price cannot be changed");
            }
            if (fee < 0f || fee > slider.m_Max)
            {
                return BridgeResponse.Error(BridgeErrorKind.InvalidArguments,
                    $"fee {fee} is outside [0, {slider.m_Max}] for {resource}");
            }
            ServiceFeeSystem.SetFee(resource, fees, fee);
            return BridgeResponse.Json(new
            {
                resource = resource.ToString(),
                previousFee = previous,
                newFee = fee,
                sliderRange = new { min = 0f, max = slider.m_Max, defaultValue = slider.m_Default },
            });
        }
    }
}
